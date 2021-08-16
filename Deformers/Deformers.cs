using AIChara;
using BepInEx;
using BepInEx.Logging;
using ExtensibleSaveFormat;
using HarmonyLib;
using KKAPI;
using KKAPI.Chara;
using KKAPI.Maker;
using MessagePack;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Unity.Jobs;
using UnityEngine.Jobs;
using Unity.Collections;
using Unity.Burst;

[BepInDependency(KoikatuAPI.GUID, KoikatuAPI.VersionConst)]
[BepInPlugin(GUID, "Deformers", Version)]
public class Deformers : BaseUnityPlugin
{
    public const string GUID = "dainty.deformers";
    public const string Version = "0.6";
    internal static new ManualLogSource Logger;
    void Awake()
    {
        Logger = base.Logger;
        CharacterApi.RegisterExtraBehaviour<DeformersController>(GUID);
        Harmony.CreateAndPatchAll(typeof(Hooks));
        AccessoriesApi.AccessoryTransferred += AccCopy; //copying destroys clothing renderer references, somehow
    }
    void AccCopy(object sender, AccessoryTransferEventArgs e)
    {
        ChaControl chaCtrl = MakerAPI.GetCharacterControl();
        DeformersController deformersController = chaCtrl.GetComponent<DeformersController>();
        chaCtrl.StartCoroutine(deformersController.GetAllRenderers(false, true));
    }

    class Hooks
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ChaControl), nameof(ChaControl.ChangeClothes), new Type[] { typeof(int), typeof(int), typeof(bool) })]
        private static void ChangeClothesHook(ChaControl __instance, int kind, int id)
        {
            DeformersController deformersController = __instance.GetComponent<DeformersController>();
            __instance.StartCoroutine(deformersController.GetAllRenderers());
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ChaControl), nameof(ChaControl.ChangeHair), new Type[] { typeof(int), typeof(int), typeof(bool) })]
        private static void ChangeHairHook(ChaControl __instance, int kind, int id)
        {
            DeformersController deformersController = __instance.GetComponent<DeformersController>();
            __instance.StartCoroutine(deformersController.GetAllRenderers());
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ChaControl), nameof(ChaControl.ChangeHead), new Type[] { typeof(int), typeof(bool) })]
        private static void ChangeHeadHook(ChaControl __instance, int _headId, bool forceChange)
        {
            DeformersController deformersController = __instance.GetComponent<DeformersController>();
            __instance.StartCoroutine(deformersController.GetAllRenderers());
        }
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ChaControl), nameof(ChaControl.ChangeAccessory), new Type[] { typeof(int), typeof(int), typeof(int), typeof(string), typeof(bool) })]
        private static void ChangeAccessoryHook(ChaControl __instance, int slotNo, int type, int id, string parentKey, bool forceChange)
        {
            DeformersController deformersController = __instance.GetComponent<DeformersController>();
            __instance.StartCoroutine(deformersController.GetAllRenderers());
        }
    }
}



public class DeformersController : CharaCustomFunctionController
{
    public Component[] Renderers { get; private set; }
    public Dictionary<Mesh, Mesh> OrigMeshes { get; private set; }
    public List<Deformer> DeformerList { get; set; }
    List<Vector3> origVertices = new List<Vector3>();
    List<Vector3> newVertices = new List<Vector3>();
    List<Vector3> bakedVertices = new List<Vector3>();
    Mesh bakedMesh = new Mesh();
    private bool loaded = false;
    private float loadedTime = 0f;
    private List<Vector3> normals = new List<Vector3>();
    private List<Vector3> newNormals = new List<Vector3>();
    public Dictionary<Mesh, Quaternion[]> NormalDiffs { get; set; }

    public Dictionary<Mesh, int[]> DuplicateVectors { get; set; }
    public Dictionary<Mesh, NativeArray<Vector3>> ResultList = new Dictionary<Mesh, NativeArray<Vector3>>();

    private string GetPartialHierarchyPath(Transform transform)
    {
        string path = transform.name;
        while (true)
        {
            if (transform.parent == null || transform.parent == this.transform)
            {
                break;
            }
            transform = transform.parent;
            path = transform.name + "/" + path;
        }
        return path;
    }
    protected override void OnCardBeingSaved(GameMode currentGameMode)
    {
        PluginData deformData = new PluginData();

        foreach (Component renderer in Renderers)
        {
            if (renderer == null)
            {
                continue;
            }
            Mesh sharedMesh = GetMesh(renderer);
            if (sharedMesh == null)
            {
                continue;
            }
            if (!OrigMeshes.ContainsKey(sharedMesh))
            {
                continue;
            }
            if (!sharedMesh.isReadable)
            {
                continue;
            }
            List<float[]> savedVertices = new List<float[]>();
            sharedMesh.GetVertices(newVertices);
            OrigMeshes[sharedMesh].GetVertices(origVertices);
            for (var j = 0; j < newVertices.Count; j++)
            {
                if (newVertices[j] != origVertices[j])
                {
                    savedVertices.Add(new float[] { j, newVertices[j].x, newVertices[j].y, newVertices[j].z });
                }
            }

            if (savedVertices.Count > 0)
            {
                if (deformData.data.ContainsKey(GetPartialHierarchyPath(renderer.transform) + newVertices.Count))
                {
                    continue;
                }
                deformData.data.Add(GetPartialHierarchyPath(renderer.transform) + newVertices.Count, MessagePackSerializer.Serialize(savedVertices, MessagePack.Resolvers.ContractlessStandardResolver.Instance));
            }
        }
        deformData.version = 1;
        if (deformData.data.Count > 0)
        {
            SetExtendedData(deformData);
        }
        else
        {
            SetExtendedData(null);
        }
    }

    protected override void OnReload(GameMode currentGameMode, bool maintainState)
    {
        OrigMeshes = new Dictionary<Mesh, Mesh>();
        NormalDiffs = new Dictionary<Mesh, Quaternion[]>();
        DuplicateVectors = new Dictionary<Mesh, int[]>();
        loaded = false;
        StartCoroutine(GetAllRenderers(true));
    }

    protected override void OnCoordinateBeingLoaded(ChaFileCoordinate coordinate)
    {
        StartCoroutine(GetAllRenderers(false, true));
    }

    public Mesh GetMesh(Component renderer)
    {
        if (renderer is SkinnedMeshRenderer skinnedMeshRenderer)
        {
            return skinnedMeshRenderer.sharedMesh;
        }
        if (renderer is MeshRenderer meshRenderer)
        {
            if (meshRenderer.material.name.StartsWith("Filter")) //dumb way of not deforming deformers
            {
                return null;
            }
            return meshRenderer.GetComponent<MeshFilter>().sharedMesh;
        }
        return null;
    }

    public void SetMesh(Component renderer, Mesh mesh)
    {
        if (renderer is SkinnedMeshRenderer skinnedMeshRenderer)
        {
            skinnedMeshRenderer.sharedMesh = mesh;
            if (skinnedMeshRenderer.gameObject.GetComponent<Cloth>() != null)
            {
                CopyCloth(skinnedMeshRenderer.gameObject);
            }
        }
        if (renderer is MeshRenderer meshRenderer)
        {
            meshRenderer.GetComponent<MeshFilter>().sharedMesh = mesh;
        }
    }
    public static void CopyCloth(GameObject renderer) //cloth component bugs out if you replace the mesh and then disable and enable the gameobject
    {
        GameObject copy = Instantiate(renderer);
        Cloth clothCopy = copy.GetComponent<Cloth>();

        DestroyImmediate(renderer.GetComponent<Cloth>());
        Cloth clothOrig = renderer.AddComponent<Cloth>();

        string name = renderer.name;

        Transform ancestor = clothOrig.transform;
        while (ancestor.parent != null)
        {
            ancestor = ancestor.parent;
            ancestor.gameObject.SetActive(true);
            if (clothOrig.gameObject.activeInHierarchy == true)
            {
                break;
            }
        }
        foreach (PropertyInfo x in typeof(Cloth).GetProperties())
        {
            if (x.CanWrite)
                x.SetValue(clothOrig, x.GetValue(clothCopy));
        }
        clothOrig.clothSolverFrequency = clothCopy.clothSolverFrequency;
        renderer.name = name;

        copy.SetActive(false);
        Destroy(copy);
    }

    public IEnumerator GetAllRenderers(bool reload = false, bool deform = false)
    {
        yield return new WaitForSeconds(0.5f); //renderers/meshes get replaced by plugins at some point, I tried hooking the load manually and setting priority to last, doesn't work. Something async I guess.

        Renderers = transform.GetComponentsInChildren(typeof(Renderer), true);
        foreach (Component renderer in Renderers)
        {
            Mesh sharedMesh = GetMesh(renderer);
            if (sharedMesh == null)
            {
                continue;
            }
            if (OrigMeshes.ContainsKey(sharedMesh))
            {
                continue;
            }
            Mesh copyMesh = Instantiate(sharedMesh);
            if (!sharedMesh.isReadable)
            {
                Deformers.Logger.LogWarning("Cannot deform " + sharedMesh.name + ", not read/write enabled.");
                copyMesh = sharedMesh;
            }
            OrigMeshes.Add(copyMesh, sharedMesh);
            String name = sharedMesh.name;
            copyMesh.name = name;
            SetMesh(renderer, copyMesh);

            if (!sharedMesh.isReadable)
            {
                continue;
            }
            copyMesh.GetNormals(normals);
            copyMesh.RecalculateNormals();
            copyMesh.GetNormals(newNormals);
            Quaternion[] normalDiffs = new Quaternion[newNormals.Count];
            for (int i = 0; i < normals.Count; i++)
            {
                normalDiffs[i] = Quaternion.FromToRotation(newNormals[i], normals[i]);
            }
            NormalDiffs.Add(copyMesh, normalDiffs);
            copyMesh.SetNormals(normals);

            Dictionary<Vector3, int> duplicateCheck = new Dictionary<Vector3, int>();
            sharedMesh.GetVertices(origVertices);
            int[] duplicates = new int[origVertices.Count];
            for (int i = 0; i < origVertices.Count; i++)
            {
                if (duplicateCheck.ContainsKey(origVertices[i]))
                {
                    duplicates[i] = duplicateCheck[origVertices[i]];
                }
                else
                {
                    duplicateCheck.Add(origVertices[i], i);
                }
            }
            DuplicateVectors.Add(copyMesh, duplicates);
        }

        if (reload)
        {
            PluginData deformData = GetExtendedData();
            if (deformData != null)
            {
                foreach (Component renderer in Renderers)
                {
                    if (renderer == null)
                    {
                        continue;
                    }
                    Mesh sharedMesh = GetMesh(renderer);
                    if (sharedMesh == null)
                    {
                        continue;
                    }
                    if (!sharedMesh.isReadable)
                    {
                        continue;
                    }
                    sharedMesh.GetVertices(newVertices);
                    if (deformData.data.TryGetValue(GetPartialHierarchyPath(renderer.transform) + newVertices.Count, out object t))
                    {
                        List<float[]> savedVertices = MessagePackSerializer.Deserialize<List<float[]>>((byte[])t, MessagePack.Resolvers.ContractlessStandardResolver.Instance);
                        for (var i = 0; i < savedVertices.Count; i++)
                        {
                            float[] savedVertex = savedVertices[i];
                            newVertices[(int)savedVertex[0]] = new Vector3(savedVertex[1], savedVertex[2], savedVertex[3]);
                        }

                        sharedMesh.SetVertices(newVertices);

                        sharedMesh.RecalculateNormals();
                        sharedMesh.GetNormals(newNormals);
                        NativeArray<Vector3> nativeNewNormals = new NativeArray<Vector3>(newNormals.ToArray(), Allocator.Temp);
                        NativeArray<Vector3> nativeReadOnlyNewNormals = new NativeArray<Vector3>(nativeNewNormals, Allocator.Temp);
                        Quaternion[] normalDiffs = NormalDiffs[sharedMesh];
                        NativeArray<Quaternion> nativeNormalDiffs = new NativeArray<Quaternion>(normalDiffs, Allocator.Temp);
                        int[] duplicates = DuplicateVectors[sharedMesh];
                        NativeArray<int> nativeDuplicates = new NativeArray<int>(duplicates, Allocator.Temp);

                        NormalDiffsJob normalDiffsJob = new NormalDiffsJob
                        {
                            nativeNewNormals = nativeNewNormals,
                            nativeReadOnlyNewNormals = nativeReadOnlyNewNormals,
                            nativeNormalDiffs = nativeNormalDiffs
                        };
                        JobHandle diffsHandle = normalDiffsJob.Schedule(nativeNewNormals.Length, 32);

                        DuplicatesJob duplicatesJob = new DuplicatesJob
                        {
                            nativeNewNormals = nativeNewNormals,
                            nativeReadOnlyNewNormals = nativeReadOnlyNewNormals,
                            nativeDuplicates = nativeDuplicates
                        };
                        JobHandle duplicatesHandle = duplicatesJob.Schedule(nativeNewNormals.Length, 32, diffsHandle);
                        duplicatesHandle.Complete();
                        newNormals.Clear();
                        newNormals.AddRange(nativeNewNormals);
                        sharedMesh.SetNormals(newNormals);
                    }
                }
            }
            loaded = true;
            loadedTime = Time.time;
        }

        if (deform)
        {
            StartCoroutine(WaitDeform());
        }
    }

    public void DeformAll()
    {
        if (loaded == false)
        {
            return;
        }
        if ((Time.time - loadedTime) < 1)
        {
            return;
        }
        if (DeformerList == null || Renderers == null)
        {
            return;
        }
        NativeArray<JobHandle> HandeList = new NativeArray<JobHandle>(Renderers.Length, Allocator.Temp);
        for (int i = 0; i < Renderers.Length; i++) {

            Renderer renderer = (Renderer)Renderers[i];
            if (renderer == null)
            {
                continue;
            }
            Mesh sharedMesh = GetMesh(renderer);
            if (sharedMesh == null)
            {
                continue;
            }
            if (!OrigMeshes.ContainsKey(sharedMesh))
            {
                continue;
            }
            if (!sharedMesh.isReadable)
            {
                continue;
            }

            OrigMeshes[sharedMesh].GetVertices(newVertices);
            sharedMesh.SetVertices(newVertices);
            OrigMeshes[sharedMesh].GetNormals(normals);
            sharedMesh.SetNormals(normals);

            bool skip = true;
            foreach (Deformer deformer in DeformerList)
            {
                if (deformer.FilterMaterial.shader.name != "Standard")
                {
                    foreach (Material material in ((Renderer)renderer).materials)
                    {
                        if (material.shader.name == deformer.FilterMaterial.shader.name)
                        {
                            skip = false;
                            break;
                        }
                    }
                    if (skip == false) break;
                }
                else
                {
                    skip = false;
                    break;
                }
            }
            if (skip) continue;

            Transform rendererTransform = renderer.transform;
            Transform parent = rendererTransform.parent;
            Vector3 localScale = rendererTransform.localScale;

            Matrix4x4[] boneMatrices = null;
            BoneWeight[] boneWeights = null;
            NativeArray<Vector3> nativeNewVertices = new NativeArray<Vector3>(newVertices.ToArray(), Allocator.Temp);
            NativeArray<Matrix4x4> nativeboneMatrices = default;
            NativeArray<BoneWeight> nativeBoneWeights = default;

            Matrix4x4 rootBoneWorldToLocalMatrix = new Matrix4x4();
            bool skinned = false;

            if (renderer is SkinnedMeshRenderer skinnedMeshRenderer)
            {
                skinned = true;

                rendererTransform.parent = null;
                localScale = rendererTransform.localScale;
                rendererTransform.localScale = Vector3.one;

                Transform[] skinnedBones = skinnedMeshRenderer.bones;
                boneMatrices = new Matrix4x4[skinnedBones.Length];
                boneWeights = skinnedMeshRenderer.sharedMesh.boneWeights;
                Matrix4x4[] meshBindposes = skinnedMeshRenderer.sharedMesh.bindposes;

                if (boneWeights.Length > 0)
                {
                    skinnedMeshRenderer.BakeMesh(bakedMesh);
                    bakedMesh.GetVertices(bakedVertices);

                    for (int j = 0; j < boneMatrices.Length; j++)
                    {
                        if (skinnedBones[j] != null && meshBindposes[j] != null)
                        {
                            boneMatrices[j] = skinnedBones[j].localToWorldMatrix * meshBindposes[j];
                        }
                        else
                        {
                            boneMatrices[j] = Matrix4x4.identity;
                        }
                    }
                    nativeboneMatrices = new NativeArray<Matrix4x4>(boneMatrices, Allocator.Temp);
                    nativeBoneWeights = new NativeArray<BoneWeight>(boneWeights, Allocator.Temp);
                }
                else
                {
                    bakedVertices = new List<Vector3>(newVertices);
                    Transform rootBone = skinnedMeshRenderer.rootBone;
                    rootBoneWorldToLocalMatrix = rootBone.worldToLocalMatrix;
                    for (int j = 0; j < bakedVertices.Count; j++)
                    {
                        bakedVertices[j] = rootBone.TransformPoint(bakedVertices[j]);
                        bakedVertices[j] = rendererTransform.InverseTransformPoint(bakedVertices[j]);
                    }
                }
            }
            else if (renderer is MeshRenderer meshRenderer)
            {
                bakedVertices = new List<Vector3>(newVertices);
                for (int j = 0; j < bakedVertices.Count; j++)
                {
                    bakedVertices[j] = rendererTransform.TransformPoint(bakedVertices[j]);
                }
            }
            else
            {
                continue;
            }

            NativeArray<Vector3> nativeBakedVertices = new NativeArray<Vector3>(bakedVertices.ToArray(), Allocator.Temp);
            NativeArray<bool> deformed = new NativeArray<bool>(1, Allocator.Temp);
            deformed[0] = false;
            NativeArray<Vector3> nativeNormals = new NativeArray<Vector3>(normals.ToArray(), Allocator.Temp);
            foreach (Deformer deformer in DeformerList)
            {
                if (!deformer.gameObject.activeInHierarchy)
                {
                    continue;
                }
                if (deformer.FilterMaterial.shader.name != "Standard")
                {
                    foreach (Material material in (renderer).materials)
                    {
                        if (material.shader.name == deformer.FilterMaterial.shader.name)
                        {
                            deformer.Deform(nativeNewVertices, nativeBakedVertices, nativeboneMatrices, nativeBoneWeights, rendererTransform.localToWorldMatrix, rendererTransform.worldToLocalMatrix, rootBoneWorldToLocalMatrix, skinned, deformed, nativeNormals);
                            break;
                        }
                    }
                }
                else
                {
                    deformer.Deform(nativeNewVertices, nativeBakedVertices, nativeboneMatrices, nativeBoneWeights, rendererTransform.localToWorldMatrix, rendererTransform.worldToLocalMatrix, rootBoneWorldToLocalMatrix, skinned, deformed, nativeNormals);
                }
            }

            rendererTransform.localScale = localScale;
            rendererTransform.parent = parent;
            if (!deformed[0])
            {
                continue;
            }
            newVertices.Clear();
            newVertices.AddRange(nativeNewVertices);
            sharedMesh.SetVertices(newVertices);

            sharedMesh.RecalculateNormals();
            sharedMesh.GetNormals(newNormals);
            NativeArray<Vector3> nativeNewNormals = new NativeArray<Vector3>(newNormals.ToArray(), Allocator.Temp);
            NativeArray<Vector3> nativeReadOnlyNewNormals = new NativeArray<Vector3>(nativeNewNormals, Allocator.Temp);
            Quaternion[] normalDiffs = NormalDiffs[sharedMesh];
            NativeArray<Quaternion> nativeNormalDiffs = new NativeArray<Quaternion>(normalDiffs, Allocator.Temp);
            int[] duplicates = DuplicateVectors[sharedMesh];
            NativeArray<int> nativeDuplicates = new NativeArray<int>(duplicates, Allocator.Temp);

            NormalDiffsJob normalDiffsJob = new NormalDiffsJob
            {
                nativeNewNormals = nativeNewNormals,
                nativeReadOnlyNewNormals = nativeReadOnlyNewNormals,
                nativeNormalDiffs = nativeNormalDiffs
            };
            JobHandle diffsHandle = normalDiffsJob.Schedule(nativeNewNormals.Length, 32);

            DuplicatesJob duplicatesJob = new DuplicatesJob
            {
                nativeNewNormals = nativeNewNormals,
                nativeReadOnlyNewNormals = nativeReadOnlyNewNormals,
                nativeDuplicates = nativeDuplicates
            };
            JobHandle duplicatesHandle = duplicatesJob.Schedule(nativeNewNormals.Length, 32, diffsHandle);
            HandeList[i] = duplicatesHandle;
            ResultList.Add(sharedMesh, nativeNewNormals);
        }
        JobHandle.CompleteAll(HandeList);
        foreach (KeyValuePair<Mesh, NativeArray<Vector3>> result in ResultList)
        {
            newNormals.Clear();
            newNormals.AddRange(result.Value);
            result.Key.SetNormals(newNormals);
        }
        ResultList.Clear();
    }

    [BurstCompile]
    public struct NormalDiffsJob : IJobParallelFor
    {
        public NativeArray<Vector3> nativeNewNormals;
        public NativeArray<Vector3> nativeReadOnlyNewNormals;
        public NativeArray<Quaternion> nativeNormalDiffs;

        public void Execute(int index)
        {
            nativeNewNormals[index] = nativeNormalDiffs[index] * nativeNewNormals[index];
            nativeReadOnlyNewNormals[index] = nativeNewNormals[index];
        }
    }

    [BurstCompile]
    public struct DuplicatesJob : IJobParallelFor
    {
        public NativeArray<Vector3> nativeNewNormals;
        public NativeArray<Vector3> nativeReadOnlyNewNormals;
        public NativeArray<int> nativeDuplicates;

        public void Execute(int index)
        {
            if (nativeDuplicates[index] != 0)
            {
                nativeNewNormals[index] = nativeReadOnlyNewNormals[nativeDuplicates[index]];
            }
        }
    }

    internal void AddDeformer(Deformer deformer)
    {
        if (DeformerList == null)
        {
            DeformerList = new List<Deformer>();
        }
        DeformerList.Add(deformer);
        DeformerList.Sort((x, y) => x.AccessoryIndex.CompareTo(y.AccessoryIndex)); //sort deformers top to bottom
        StartCoroutine(WaitDeform());
    }

    internal void RemoveDeformer(Deformer deformer)
    {
        DeformerList.Remove(deformer);
        if (gameObject.activeInHierarchy)
        {
            StartCoroutine(WaitDeform());
        }
    }
    internal IEnumerator WaitDeform() //when stuff happens before skinning
    {
        yield return new WaitForEndOfFrame();
        DeformAll();
    }
}

public abstract class Deformer : MonoBehaviour
{
    public Transform N1;
    public Transform N2;
    public Transform SphereA;
    public Transform SphereB;
    private Vector3 oldN1Position = Vector3.zero;
    private Vector3 oldN1Rotation = Vector3.zero;
    private Vector3 oldN1Scale = Vector3.zero;
    private Vector3 oldN2Position = Vector3.zero;
    private Vector3 oldN2Rotation = Vector3.zero;
    private Vector3 oldN2Scale = Vector3.zero;
    public DeformersController deformersController;
    public int AccessoryIndex { get; internal set; }
    public Material FilterMaterial { get; internal set; }
    public string oldShaderName { get; internal set; }
    public float radius = 1.09f;
    public float falloff = 1;
    public float strength = 0.2f;
    public float axisWeight = 1f;
    public float height = 1f;
    public Vector3 point1;
    public Vector3 point2;
    public Vector3 sphereA;
    public Vector3 sphereB;
    public SelectorType selectorType = SelectorType.Sphere;

    public enum SelectorType
    {
        Sphere,
        Capsule
    }

    void Start()
    {
        N1 = GetComponent<CmpAccessory>().trfMove01;
        N2 = GetComponent<CmpAccessory>().trfMove02;

        FilterMaterial = ((Renderer)N1.GetComponentInChildren<Renderer>(true)).materials[0]; //renderer is disabled when alpha = 0
        oldShaderName = FilterMaterial.shader.name;

        oldN1Position = N1.localPosition;
        oldN1Rotation = N1.localEulerAngles;
        oldN1Scale = N1.localScale;

        oldN2Position = N2.localPosition;
        oldN2Rotation = N2.localEulerAngles;
        oldN2Scale = N2.localScale;

        Transform ancestor = transform;
        while (ancestor.parent != null)
        {
            ancestor = ancestor.parent;
            if (ancestor.GetComponent<DeformersController>() != null)
            {
                deformersController = ancestor.GetComponent<DeformersController>();
                break;
            }
        }
        AccessoryIndex = AccessoriesApi.GetAccessoryIndex(deformersController.GetComponent<ChaControl>(), gameObject);
        deformersController.AddDeformer(this);
    }

    void OnDestroy()
    {
        deformersController.RemoveDeformer(this);
    }

    void OnDisable()
    {
        deformersController.DeformAll();
    }

    void OnEnable()
    {
        if(deformersController != null)
        {
            deformersController.DeformAll();
        }
    }

    void Update()
    {
        if (N1.localScale != oldN1Scale)
        {
            Vector3[,] addMove = deformersController.GetComponent<ChaControl>().nowCoordinate.accessory.parts[AccessoryIndex].addMove;
            Vector3 scale = N1.localScale;
            if (selectorType == SelectorType.Sphere)
            {
                scale = new Vector3(N1.localScale.x, N1.localScale.x, N1.localScale.x);
                N1.localScale = scale;
            }
            if (selectorType == SelectorType.Capsule)
            {
                scale = new Vector3(N1.localScale.x, N1.localScale.y, N1.localScale.x);
                N1.localScale = scale;
                SphereA.localScale = new Vector3(1 / N1.localScale.x, 1 / N1.localScale.y, 1 / N1.localScale.z) * N1.localScale.x;
                SphereB.localScale = SphereA.localScale;
            }
            addMove[0, 2] = scale;
            deformersController.GetComponent<ChaControl>().nowCoordinate.accessory.parts[AccessoryIndex].addMove = addMove;
            N1.localScale = scale;
            if (MakerAPI.InsideMaker)
            {
                AccessoriesApi.GetCvsAccessory().UpdateCustomUI();
            }
            deformersController.DeformAll();
        }
        else if (N1.localPosition != oldN1Position || N1.localEulerAngles != oldN1Rotation ||
                 N2.localPosition != oldN2Position || N2.localEulerAngles != oldN2Rotation || N2.localScale != oldN2Scale ||
                 oldShaderName != FilterMaterial.shader.name)
        {
            deformersController.DeformAll();
        }

        oldN1Position = N1.localPosition;
        oldN1Rotation = N1.localEulerAngles;
        oldN1Scale = N1.localScale;

        oldN2Position = N2.localPosition;
        oldN2Rotation = N2.localEulerAngles;
        oldN2Scale = N2.localScale;

        oldShaderName = FilterMaterial.shader.name;
    }

    public abstract void Deform(NativeArray<Vector3> nativeNewVertices, NativeArray<Vector3> nativeBakedVertices, NativeArray<Matrix4x4> nativeBoneMatrices, NativeArray<BoneWeight> nativeBoneWeights, Matrix4x4 RendererLocalToWorldMatrix, Matrix4x4 RendererWorldToLocalMatrix, Matrix4x4 RootBoneWorldToLocalMatrix, bool skinned, NativeArray<bool> deformed, NativeArray<Vector3> nativeNormals);

    public static Matrix4x4 GetReverseSkinningMatrix(NativeArray<Matrix4x4> boneMatrices, BoneWeight weight)
    {
        Matrix4x4 bm0;
        Matrix4x4 bm1;
        Matrix4x4 bm2;
        Matrix4x4 bm3;
        Matrix4x4 reverseSkinningMatrix = new Matrix4x4();
        bm0 = boneMatrices[weight.boneIndex0].inverse;
        bm1 = boneMatrices[weight.boneIndex1].inverse;
        bm2 = boneMatrices[weight.boneIndex2].inverse;
        bm3 = boneMatrices[weight.boneIndex3].inverse;

        reverseSkinningMatrix.m00 = bm0.m00 * weight.weight0 + bm1.m00 * weight.weight1 + bm2.m00 * weight.weight2 + bm3.m00 * weight.weight3;
        reverseSkinningMatrix.m01 = bm0.m01 * weight.weight0 + bm1.m01 * weight.weight1 + bm2.m01 * weight.weight2 + bm3.m01 * weight.weight3;
        reverseSkinningMatrix.m02 = bm0.m02 * weight.weight0 + bm1.m02 * weight.weight1 + bm2.m02 * weight.weight2 + bm3.m02 * weight.weight3;
        reverseSkinningMatrix.m03 = bm0.m03 * weight.weight0 + bm1.m03 * weight.weight1 + bm2.m03 * weight.weight2 + bm3.m03 * weight.weight3;

        reverseSkinningMatrix.m10 = bm0.m10 * weight.weight0 + bm1.m10 * weight.weight1 + bm2.m10 * weight.weight2 + bm3.m10 * weight.weight3;
        reverseSkinningMatrix.m11 = bm0.m11 * weight.weight0 + bm1.m11 * weight.weight1 + bm2.m11 * weight.weight2 + bm3.m11 * weight.weight3;
        reverseSkinningMatrix.m12 = bm0.m12 * weight.weight0 + bm1.m12 * weight.weight1 + bm2.m12 * weight.weight2 + bm3.m12 * weight.weight3;
        reverseSkinningMatrix.m13 = bm0.m13 * weight.weight0 + bm1.m13 * weight.weight1 + bm2.m13 * weight.weight2 + bm3.m13 * weight.weight3;

        reverseSkinningMatrix.m20 = bm0.m20 * weight.weight0 + bm1.m20 * weight.weight1 + bm2.m20 * weight.weight2 + bm3.m20 * weight.weight3;
        reverseSkinningMatrix.m21 = bm0.m21 * weight.weight0 + bm1.m21 * weight.weight1 + bm2.m21 * weight.weight2 + bm3.m21 * weight.weight3;
        reverseSkinningMatrix.m22 = bm0.m22 * weight.weight0 + bm1.m22 * weight.weight1 + bm2.m22 * weight.weight2 + bm3.m22 * weight.weight3;
        reverseSkinningMatrix.m23 = bm0.m23 * weight.weight0 + bm1.m23 * weight.weight1 + bm2.m23 * weight.weight2 + bm3.m23 * weight.weight3;

        return reverseSkinningMatrix;
    }

    public static Vector3 BakedToNewVertex(Vector3 bakedVertex, Matrix4x4 RendererWorldToLocalMatrix, Matrix4x4 RendererLocalToWorldMatrix, Matrix4x4 RootBoneWorldToLocalMatrix, NativeArray<Matrix4x4> boneMatrices, NativeArray<BoneWeight> boneWeights, int index, bool skinned)
    {
        Vector3 newVertex;
        if (skinned == false)
        {
            newVertex = RendererWorldToLocalMatrix.MultiplyPoint3x4(bakedVertex);
            return newVertex;
        }
        if (boneWeights.Length > 0)
        {
            BoneWeight weight = boneWeights[index];
            Matrix4x4 reverseSkinningMatrix = GetReverseSkinningMatrix(boneMatrices, weight);
            newVertex = reverseSkinningMatrix.MultiplyPoint3x4(RendererLocalToWorldMatrix.MultiplyPoint3x4(bakedVertex));
        }
        else
        {
            Vector3 v = RendererLocalToWorldMatrix.MultiplyPoint3x4(bakedVertex);
            newVertex = RootBoneWorldToLocalMatrix.MultiplyPoint3x4(v);
        }
        return newVertex;
    }

    public void SetDeformParams()
    {
        radius = N1.localScale.x / 2;
        strength = N2.localScale.x;
        height = (N1.localScale.y / 2) + radius;
        falloff = N2.localScale.y;
        axisWeight = N2.localScale.z;
        if (falloff == 0.01f)
        {
            falloff = 0f;
        }
        if (strength == 0.01f)
        {
            strength = 0f;
        }
        if (axisWeight == 0.01f)
        {
            axisWeight = 0f;
        }
    }

    public void SetPoints(Matrix4x4 RendererWorldToLocalMatrix, bool skinned)
    {
        point1 = N1.position;
        point2 = N2.position;
        if (skinned)
        {
            point1 = RendererWorldToLocalMatrix.MultiplyPoint3x4(point1);
            point2 = RendererWorldToLocalMatrix.MultiplyPoint3x4(point2);
        }
        if (selectorType == SelectorType.Capsule)
        {
            sphereA = SphereA.position;
            sphereB = SphereB.position;
            if (skinned)
            {
                sphereA = RendererWorldToLocalMatrix.MultiplyPoint3x4(sphereA);
                sphereB = RendererWorldToLocalMatrix.MultiplyPoint3x4(sphereB);
            }
        }
    }
}

public class Expander : Deformer
{
    [BurstCompile]
    public struct DeformJob : IJobParallelFor
    {
        public float radius;
        public float falloff;
        public float strength;
        public NativeArray<Vector3> nativeNewVertices;
        public NativeArray<Vector3> nativeBakedVertices;
        public Vector3 point1;
        public Vector3 point2;
        public NativeArray<Matrix4x4> nativeboneMatrices;
        public NativeArray<BoneWeight> nativeBoneWeights;
        public Matrix4x4 RendererWorldToLocalMatrix;
        public Matrix4x4 RendererLocalToWorldMatrix;
        public Matrix4x4 RootBoneWorldToLocalMatrix;
        public bool skinned;
        public NativeArray<bool> deformed;
        public SelectorType selectorType;
        public Vector3 sphereA;
        public Vector3 sphereB;
        public float height;
        public float axisWeight;
        public NativeArray<Vector3> nativeNormals;

        public void Execute(int index)
        {
            if (selectorType == SelectorType.Sphere)
            {
                float distance = Vector3.Distance(nativeBakedVertices[index], point1);
                if (distance < radius)
                {
                    deformed[0] = true;
                    float factor = 1 - ((distance / radius) * falloff);
                    nativeBakedVertices[index] = Vector3.Lerp(nativeBakedVertices[index], nativeBakedVertices[index] + (nativeNormals[index].normalized / 10), strength * factor);
                    nativeNewVertices[index] = BakedToNewVertex(nativeBakedVertices[index], RendererWorldToLocalMatrix, RendererLocalToWorldMatrix, RootBoneWorldToLocalMatrix, nativeboneMatrices, nativeBoneWeights, index, skinned);
                }
            }
            if (selectorType == SelectorType.Capsule)
            {
                Vector3 sphereVector = sphereB - sphereA;
                Vector3 capsuleEndA = sphereA - (radius * sphereVector.normalized);
                Vector3 capsuleEndB = sphereB + (radius * sphereVector.normalized);
                Vector3 capsuleVector = capsuleEndB - capsuleEndA;
                Vector3 p = nativeBakedVertices[index] - capsuleEndA;
                float dot = p.x * capsuleVector.x + p.y * capsuleVector.y + p.z * capsuleVector.z;
                float lengthsq = capsuleVector.sqrMagnitude;
                if ((dot > 0f) && (dot < lengthsq))
                {
                    Vector3 closestPointOnAxis;
                    float f = Vector3.Dot(nativeBakedVertices[index] - sphereA, sphereVector.normalized);
                    if (f < 0)
                    {
                        closestPointOnAxis = sphereA;
                    }
                    else if (f > sphereVector.magnitude)
                    {
                        closestPointOnAxis = sphereB;
                    }
                    else closestPointOnAxis = sphereA + (f * sphereVector.normalized);

                    float axisDistance = Vector3.Distance(closestPointOnAxis, nativeBakedVertices[index]);
                    if (axisDistance < radius)
                    {
                        deformed[0] = true;
                        closestPointOnAxis = sphereA + (f * sphereVector.normalized);
                        axisDistance = Vector3.Distance(closestPointOnAxis, nativeBakedVertices[index]);
                        float axisFactor = 1 - ((axisDistance / radius) * falloff);

                        float centerDistance = Vector3.Distance(point1, nativeBakedVertices[index]);
                        float centerFactor = 1 - ((centerDistance / height) * falloff);

                        float factor = Mathf.Lerp(axisFactor, centerFactor, axisWeight);
                        nativeBakedVertices[index] = Vector3.Lerp(nativeBakedVertices[index], nativeBakedVertices[index] + (nativeNormals[index].normalized / 10), strength * factor);
                        nativeNewVertices[index] = BakedToNewVertex(nativeBakedVertices[index], RendererWorldToLocalMatrix, RendererLocalToWorldMatrix, RootBoneWorldToLocalMatrix, nativeboneMatrices, nativeBoneWeights, index, skinned);
                    }
                }
            }
        }
    }

    public override void Deform(NativeArray<Vector3> nativeNewVertices, NativeArray<Vector3> nativeBakedVertices, NativeArray<Matrix4x4> nativeboneMatrices, NativeArray<BoneWeight> nativeBoneWeights, Matrix4x4 RendererLocalToWorldMatrix, Matrix4x4 RendererWorldToLocalMatrix, Matrix4x4 RootBoneWorldToLocalMatrix, bool skinned, NativeArray<bool> deformed, NativeArray<Vector3> nativeNormals)
    {
        SetDeformParams();
        SetPoints(RendererWorldToLocalMatrix, skinned);

        DeformJob deformJob = new DeformJob
        {
            nativeNewVertices = nativeNewVertices,
            nativeBakedVertices = nativeBakedVertices,
            radius = radius,
            falloff = falloff,
            strength = strength,
            point1 = point1,
            point2 = point2,
            RendererLocalToWorldMatrix = RendererLocalToWorldMatrix,
            RendererWorldToLocalMatrix = RendererWorldToLocalMatrix,
            RootBoneWorldToLocalMatrix = RootBoneWorldToLocalMatrix,
            nativeBoneWeights = nativeBoneWeights,
            nativeboneMatrices = nativeboneMatrices,
            skinned = skinned,
            deformed = deformed,
            selectorType = selectorType,
            sphereA = sphereA,
            sphereB = sphereB,
            axisWeight = axisWeight,
            height = height,
            nativeNormals = nativeNormals
        };

        JobHandle jobHandle = deformJob.Schedule(nativeNewVertices.Length, 32);
        jobHandle.Complete();
    }
}

public class Shrinker : Deformer
{
    [BurstCompile]
    public struct DeformJob : IJobParallelFor
    {
        public float radius;
        public float falloff;
        public float strength;
        public NativeArray<Vector3> nativeNewVertices;
        public NativeArray<Vector3> nativeBakedVertices;
        public Vector3 point1;
        public Vector3 point2;
        public NativeArray<Matrix4x4> nativeboneMatrices;
        public NativeArray<BoneWeight> nativeBoneWeights;
        public Matrix4x4 RendererWorldToLocalMatrix;
        public Matrix4x4 RendererLocalToWorldMatrix;
        public Matrix4x4 RootBoneWorldToLocalMatrix;
        public bool skinned;
        public NativeArray<bool> deformed;
        public SelectorType selectorType;
        public Vector3 sphereA;
        public Vector3 sphereB;
        public float height;
        public float axisWeight;
        public NativeArray<Vector3> nativeNormals;

        public void Execute(int index)
        {
            if (selectorType == SelectorType.Sphere)
            {
                float distance = Vector3.Distance(nativeBakedVertices[index], point1);
                if (distance < radius)
                {
                    deformed[0] = true;
                    float factor = 1 - ((distance / radius) * falloff);
                    nativeBakedVertices[index] = Vector3.Lerp(nativeBakedVertices[index], nativeBakedVertices[index] - (nativeNormals[index].normalized/10), strength * factor);
                    nativeNewVertices[index] = BakedToNewVertex(nativeBakedVertices[index], RendererWorldToLocalMatrix, RendererLocalToWorldMatrix, RootBoneWorldToLocalMatrix, nativeboneMatrices, nativeBoneWeights, index, skinned);
                }
            }
            if (selectorType == SelectorType.Capsule)
            {
                Vector3 sphereVector = sphereB - sphereA;
                Vector3 capsuleEndA = sphereA - (radius * sphereVector.normalized);
                Vector3 capsuleEndB = sphereB + (radius * sphereVector.normalized);
                Vector3 capsuleVector = capsuleEndB - capsuleEndA;
                Vector3 p = nativeBakedVertices[index] - capsuleEndA;
                float dot = p.x * capsuleVector.x + p.y * capsuleVector.y + p.z * capsuleVector.z;
                float lengthsq = capsuleVector.sqrMagnitude;
                if ((dot > 0f) && (dot < lengthsq))
                {
                    Vector3 closestPointOnAxis;
                    float f = Vector3.Dot(nativeBakedVertices[index] - sphereA, sphereVector.normalized);
                    if (f < 0)
                    {
                        closestPointOnAxis = sphereA;
                    }
                    else if (f > sphereVector.magnitude)
                    {
                        closestPointOnAxis = sphereB;
                    }
                    else closestPointOnAxis = sphereA + (f * sphereVector.normalized);

                    float axisDistance = Vector3.Distance(closestPointOnAxis, nativeBakedVertices[index]);
                    if (axisDistance < radius)
                    {
                        deformed[0] = true;
                        closestPointOnAxis = sphereA + (f * sphereVector.normalized);
                        axisDistance = Vector3.Distance(closestPointOnAxis, nativeBakedVertices[index]);
                        float axisFactor = 1 - ((axisDistance / radius) * falloff);

                        float centerDistance = Vector3.Distance(point1, nativeBakedVertices[index]);
                        float centerFactor = 1 - ((centerDistance / height) * falloff);

                        float factor = Mathf.Lerp(axisFactor, centerFactor, axisWeight);
                        nativeBakedVertices[index] = Vector3.Lerp(nativeBakedVertices[index], nativeBakedVertices[index] - (nativeNormals[index].normalized/10), strength * factor);
                        nativeNewVertices[index] = BakedToNewVertex(nativeBakedVertices[index], RendererWorldToLocalMatrix, RendererLocalToWorldMatrix, RootBoneWorldToLocalMatrix, nativeboneMatrices, nativeBoneWeights, index, skinned);
                    }
                }
            }
        }
    }

    public override void Deform(NativeArray<Vector3> nativeNewVertices, NativeArray<Vector3> nativeBakedVertices, NativeArray<Matrix4x4> nativeboneMatrices, NativeArray<BoneWeight> nativeBoneWeights, Matrix4x4 RendererLocalToWorldMatrix, Matrix4x4 RendererWorldToLocalMatrix, Matrix4x4 RootBoneWorldToLocalMatrix, bool skinned, NativeArray<bool> deformed, NativeArray<Vector3> nativeNormals)
    {
        SetDeformParams();
        SetPoints(RendererWorldToLocalMatrix, skinned);

        DeformJob deformJob = new DeformJob
        {
            nativeNewVertices = nativeNewVertices,
            nativeBakedVertices = nativeBakedVertices,
            radius = radius,
            falloff = falloff,
            strength = strength,
            point1 = point1,
            point2 = point2,
            RendererLocalToWorldMatrix = RendererLocalToWorldMatrix,
            RendererWorldToLocalMatrix = RendererWorldToLocalMatrix,
            RootBoneWorldToLocalMatrix = RootBoneWorldToLocalMatrix,
            nativeBoneWeights = nativeBoneWeights,
            nativeboneMatrices = nativeboneMatrices,
            skinned = skinned,
            deformed = deformed,
            selectorType = selectorType,
            sphereA = sphereA,
            sphereB = sphereB,
            axisWeight = axisWeight,
            height = height,
            nativeNormals = nativeNormals
        };

        JobHandle jobHandle = deformJob.Schedule(nativeNewVertices.Length, 32);
        jobHandle.Complete();
    }
}

public class Squeezer : Deformer
{
    [BurstCompile]
    public struct DeformJob : IJobParallelFor
    {
        public float radius;
        public float falloff;
        public float strength;
        public NativeArray<Vector3> nativeNewVertices;
        public NativeArray<Vector3> nativeBakedVertices;
        public Vector3 point1;
        public Vector3 point2;
        public NativeArray<Matrix4x4> nativeboneMatrices;
        public NativeArray<BoneWeight> nativeBoneWeights;
        public Matrix4x4 RendererWorldToLocalMatrix;
        public Matrix4x4 RendererLocalToWorldMatrix;
        public Matrix4x4 RootBoneWorldToLocalMatrix;
        public bool skinned;
        public NativeArray<bool> deformed;
        public SelectorType selectorType;
        public Vector3 sphereA;
        public Vector3 sphereB;
        public float height;
        public float axisWeight;

        public void Execute(int index)
        {
            if(selectorType == SelectorType.Sphere)
            {
                float distance = Vector3.Distance(nativeBakedVertices[index], point1);
                if (distance < radius)
                {
                    deformed[0] = true;
                    float factor = 1 - ((distance / radius) * falloff);
                    nativeBakedVertices[index] = Vector3.Lerp(nativeBakedVertices[index], point2, strength * factor);
                    nativeNewVertices[index] = BakedToNewVertex(nativeBakedVertices[index], RendererWorldToLocalMatrix, RendererLocalToWorldMatrix, RootBoneWorldToLocalMatrix, nativeboneMatrices, nativeBoneWeights, index, skinned);
                }
            }
            if (selectorType == SelectorType.Capsule)
            {
                Vector3 sphereVector = sphereB - sphereA;
                Vector3 capsuleEndA = sphereA - (radius * sphereVector.normalized);
                Vector3 capsuleEndB = sphereB + (radius * sphereVector.normalized);
                Vector3 capsuleVector = capsuleEndB - capsuleEndA;
                Vector3 p = nativeBakedVertices[index] - capsuleEndA;
                float dot = p.x * capsuleVector.x + p.y * capsuleVector.y + p.z * capsuleVector.z;
                float lengthsq = capsuleVector.sqrMagnitude;
                if ((dot > 0f) && (dot < lengthsq))
                {
                    Vector3 closestPointOnAxis;
                    float f = Vector3.Dot(nativeBakedVertices[index] - sphereA, sphereVector.normalized);
                    if (f < 0)
                    {
                        closestPointOnAxis = sphereA;
                    }
                    else if (f > sphereVector.magnitude)
                    {
                        closestPointOnAxis = sphereB;
                    }
                    else closestPointOnAxis = sphereA + (f * sphereVector.normalized);

                    float axisDistance = Vector3.Distance(closestPointOnAxis, nativeBakedVertices[index]);
                    if (axisDistance < radius)
                    {
                        deformed[0] = true;
                        closestPointOnAxis = sphereA + (f * sphereVector.normalized);
                        axisDistance = Vector3.Distance(closestPointOnAxis, nativeBakedVertices[index]);
                        float axisFactor = 1 - ((axisDistance / radius) * falloff);

                        float centerDistance = Vector3.Distance(point1, nativeBakedVertices[index]);
                        float centerFactor = 1 - ((centerDistance / height) * falloff);

                        float factor = Mathf.Lerp(axisFactor, centerFactor, axisWeight);
                        closestPointOnAxis = Vector3.Lerp(closestPointOnAxis, point1, axisWeight);
                        nativeBakedVertices[index] = Vector3.Lerp(nativeBakedVertices[index], closestPointOnAxis + (point2 - point1), strength * factor);
                        nativeNewVertices[index] = BakedToNewVertex(nativeBakedVertices[index], RendererWorldToLocalMatrix, RendererLocalToWorldMatrix, RootBoneWorldToLocalMatrix, nativeboneMatrices, nativeBoneWeights, index, skinned);
                    }
                }
            }
        }
    }

    public override void Deform(NativeArray<Vector3> nativeNewVertices, NativeArray<Vector3> nativeBakedVertices, NativeArray<Matrix4x4> nativeboneMatrices, NativeArray<BoneWeight> nativeBoneWeights, Matrix4x4 RendererLocalToWorldMatrix, Matrix4x4 RendererWorldToLocalMatrix, Matrix4x4 RootBoneWorldToLocalMatrix, bool skinned, NativeArray<bool> deformed, NativeArray<Vector3> nativeNormals)
    {
        SetDeformParams();
        SetPoints(RendererWorldToLocalMatrix, skinned);

        DeformJob deformJob = new DeformJob
        {
            nativeNewVertices = nativeNewVertices,
            nativeBakedVertices = nativeBakedVertices,
            radius = radius,
            falloff = falloff,
            strength = strength,
            point1 = point1,
            point2 = point2,
            RendererLocalToWorldMatrix = RendererLocalToWorldMatrix,
            RendererWorldToLocalMatrix = RendererWorldToLocalMatrix,
            RootBoneWorldToLocalMatrix = RootBoneWorldToLocalMatrix,
            nativeBoneWeights = nativeBoneWeights,
            nativeboneMatrices = nativeboneMatrices,
            skinned = skinned,
            deformed = deformed,
            selectorType = selectorType,
            sphereA = sphereA,
            sphereB = sphereB,
            axisWeight = axisWeight,
            height = height
        };

        JobHandle jobHandle = deformJob.Schedule(nativeNewVertices.Length, 32);
        jobHandle.Complete();
    }
}

public class Bulger : Deformer
{

    [BurstCompile]
    public struct DeformJob : IJobParallelFor
    {
        public float radius;
        public float falloff;
        public float strength;
        public NativeArray<Vector3> nativeNewVertices;
        public NativeArray<Vector3> nativeBakedVertices;
        public Vector3 point1;
        public Vector3 point2;
        public NativeArray<Matrix4x4> nativeboneMatrices;
        public NativeArray<BoneWeight> nativeBoneWeights;
        public Matrix4x4 RendererWorldToLocalMatrix;
        public Matrix4x4 RendererLocalToWorldMatrix;
        public Matrix4x4 RootBoneWorldToLocalMatrix;
        public bool skinned;
        public NativeArray<bool> deformed;
        public SelectorType selectorType;
        public Vector3 sphereA;
        public Vector3 sphereB;
        public float height;
        public float axisWeight;

        public void Execute(int index)
        {
            if (selectorType == SelectorType.Sphere)
            {
                float distance = Vector3.Distance(nativeBakedVertices[index], point1);
                if (distance < radius)
                {
                    deformed[0] = true;
                    Vector3 newPoint = nativeBakedVertices[index] + (nativeBakedVertices[index] - point2);
                    float factor = 1 - ((distance / radius) * falloff);
                    nativeBakedVertices[index] = Vector3.Lerp(nativeBakedVertices[index], newPoint, strength * factor);
                    nativeNewVertices[index] = BakedToNewVertex(nativeBakedVertices[index], RendererWorldToLocalMatrix, RendererLocalToWorldMatrix, RootBoneWorldToLocalMatrix, nativeboneMatrices, nativeBoneWeights, index, skinned);
                }
            }
            if (selectorType == SelectorType.Capsule)
            {
                Vector3 sphereVector = sphereB - sphereA;
                Vector3 capsuleEndA = sphereA - (radius * sphereVector.normalized);
                Vector3 capsuleEndB = sphereB + (radius * sphereVector.normalized);
                Vector3 capsuleVector = capsuleEndB - capsuleEndA;
                Vector3 p = nativeBakedVertices[index] - capsuleEndA;
                float dot = p.x * capsuleVector.x + p.y * capsuleVector.y + p.z * capsuleVector.z;
                float lengthsq = capsuleVector.sqrMagnitude;
                if ((dot > 0f) && (dot < lengthsq))
                {
                    Vector3 closestPointOnAxis;
                    float f = Vector3.Dot(nativeBakedVertices[index] - sphereA, sphereVector.normalized);
                    if (f < 0)
                    {
                        closestPointOnAxis = sphereA;
                    }
                    else if (f > sphereVector.magnitude)
                    {
                        closestPointOnAxis = sphereB;
                    }
                    else closestPointOnAxis = sphereA + (f * sphereVector.normalized);

                    float axisDistance = Vector3.Distance(closestPointOnAxis, nativeBakedVertices[index]);
                    if (axisDistance < radius)
                    {
                        deformed[0] = true;
                        closestPointOnAxis = sphereA + (f * sphereVector.normalized);
                        axisDistance = Vector3.Distance(closestPointOnAxis, nativeBakedVertices[index]);
                        float axisFactor = 1 - ((axisDistance / radius) * falloff);

                        float centerDistance = Vector3.Distance(point1, nativeBakedVertices[index]);
                        float centerFactor = 1 - ((centerDistance / height) * falloff);

                        float factor = Mathf.Lerp(axisFactor, centerFactor, axisWeight);
                        closestPointOnAxis = Vector3.Lerp(closestPointOnAxis, point1, axisWeight);
                        Vector3 newPoint = nativeBakedVertices[index] + (nativeBakedVertices[index] - (closestPointOnAxis + (point2 - point1)));
                        nativeBakedVertices[index] = Vector3.Lerp(nativeBakedVertices[index], newPoint, strength * factor);
                        nativeNewVertices[index] = BakedToNewVertex(nativeBakedVertices[index], RendererWorldToLocalMatrix, RendererLocalToWorldMatrix, RootBoneWorldToLocalMatrix, nativeboneMatrices, nativeBoneWeights, index, skinned);
                    }
                }
            }
        }
    }

    public override void Deform(NativeArray<Vector3> nativeNewVertices, NativeArray<Vector3> nativeBakedVertices, NativeArray<Matrix4x4> nativeboneMatrices, NativeArray<BoneWeight> nativeBoneWeights, Matrix4x4 RendererLocalToWorldMatrix, Matrix4x4 RendererWorldToLocalMatrix, Matrix4x4 RootBoneWorldToLocalMatrix, bool skinned, NativeArray<bool> deformed, NativeArray<Vector3> nativeNormals)
    {
        SetDeformParams();
        SetPoints(RendererWorldToLocalMatrix, skinned);

        DeformJob deformJob = new DeformJob
        {
            nativeNewVertices = nativeNewVertices,
            nativeBakedVertices = nativeBakedVertices,
            radius = radius,
            falloff = falloff,
            strength = strength,
            point1 = point1,
            point2 = point2,
            RendererLocalToWorldMatrix = RendererLocalToWorldMatrix,
            RendererWorldToLocalMatrix = RendererWorldToLocalMatrix,
            RootBoneWorldToLocalMatrix = RootBoneWorldToLocalMatrix,
            nativeBoneWeights = nativeBoneWeights,
            nativeboneMatrices = nativeboneMatrices,
            skinned = skinned,
            deformed = deformed,
            selectorType = selectorType,
            sphereA = sphereA,
            sphereB = sphereB,
            axisWeight = axisWeight,
            height = height
        };

        JobHandle jobHandle = deformJob.Schedule(nativeNewVertices.Length, 32);
        jobHandle.Complete();
    }
}

public class Mover : Deformer
{
    [BurstCompile]
    public struct DeformJob : IJobParallelFor
    {
        public float radius;
        public float falloff;
        public float strength;
        public NativeArray<Vector3> nativeNewVertices;
        public NativeArray<Vector3> nativeBakedVertices;
        public Vector3 point1;
        public Vector3 point2;
        public NativeArray<Matrix4x4> nativeboneMatrices;
        public NativeArray<BoneWeight> nativeBoneWeights;
        public Matrix4x4 RendererWorldToLocalMatrix;
        public Matrix4x4 RendererLocalToWorldMatrix;
        public Matrix4x4 RootBoneWorldToLocalMatrix;
        public Vector3 moveVector;
        public bool skinned;
        public NativeArray<bool> deformed;
        public SelectorType selectorType;
        public Vector3 sphereA;
        public Vector3 sphereB;
        public float height;
        public float axisWeight;

        public void Execute(int index)
        {
            if (selectorType == SelectorType.Sphere)
            {
                float distance = Vector3.Distance(nativeBakedVertices[index], point1);
                if (distance < radius)
                {
                    deformed[0] = true;
                    Vector3 newPoint = nativeBakedVertices[index] + moveVector;
                    float factor = 1 - ((distance / radius) * falloff);
                    nativeBakedVertices[index] = Vector3.Lerp(nativeBakedVertices[index], newPoint, strength * factor);
                    nativeNewVertices[index] = BakedToNewVertex(nativeBakedVertices[index], RendererWorldToLocalMatrix, RendererLocalToWorldMatrix, RootBoneWorldToLocalMatrix, nativeboneMatrices, nativeBoneWeights, index, skinned);
                }
            }
            if (selectorType == SelectorType.Capsule)
            {
                Vector3 sphereVector = sphereB - sphereA;
                Vector3 capsuleEndA = sphereA - (radius * sphereVector.normalized);
                Vector3 capsuleEndB = sphereB + (radius * sphereVector.normalized);
                Vector3 capsuleVector = capsuleEndB - capsuleEndA;
                Vector3 p = nativeBakedVertices[index] - capsuleEndA;
                float dot = p.x * capsuleVector.x + p.y * capsuleVector.y + p.z * capsuleVector.z;
                float lengthsq = capsuleVector.sqrMagnitude;
                if ((dot > 0f) && (dot < lengthsq))
                {
                    Vector3 closestPointOnAxis;
                    float f = Vector3.Dot(nativeBakedVertices[index] - sphereA, sphereVector.normalized);
                    if (f < 0)
                    {
                        closestPointOnAxis = sphereA;
                    }
                    else if (f > sphereVector.magnitude)
                    {
                        closestPointOnAxis = sphereB;
                    }
                    else closestPointOnAxis = sphereA + (f * sphereVector.normalized);

                    float axisDistance = Vector3.Distance(closestPointOnAxis, nativeBakedVertices[index]);
                    if (axisDistance < radius)
                    {
                        deformed[0] = true;
                        closestPointOnAxis = sphereA + (f * sphereVector.normalized);
                        axisDistance = Vector3.Distance(closestPointOnAxis, nativeBakedVertices[index]);
                        float axisFactor = 1 - ((axisDistance / radius) * falloff);

                        float centerDistance = Vector3.Distance(point1, nativeBakedVertices[index]);
                        float centerFactor = 1 - ((centerDistance / height) * falloff);

                        float factor = Mathf.Lerp(axisFactor, centerFactor, axisWeight);
                        Vector3 newPoint = nativeBakedVertices[index] + moveVector;
                        nativeBakedVertices[index] = Vector3.Lerp(nativeBakedVertices[index], newPoint, strength * factor);
                        nativeNewVertices[index] = BakedToNewVertex(nativeBakedVertices[index], RendererWorldToLocalMatrix, RendererLocalToWorldMatrix, RootBoneWorldToLocalMatrix, nativeboneMatrices, nativeBoneWeights, index, skinned);
                    }
                }
            }
        }
    }

    public override void Deform(NativeArray<Vector3> nativeNewVertices, NativeArray<Vector3> nativeBakedVertices, NativeArray<Matrix4x4> nativeboneMatrices, NativeArray<BoneWeight> nativeBoneWeights, Matrix4x4 RendererLocalToWorldMatrix, Matrix4x4 RendererWorldToLocalMatrix, Matrix4x4 RootBoneWorldToLocalMatrix, bool skinned, NativeArray<bool> deformed, NativeArray<Vector3> nativeNormals)
    {
        SetDeformParams();
        SetPoints(RendererWorldToLocalMatrix, skinned);

        DeformJob deformJob = new DeformJob
        {
            nativeNewVertices = nativeNewVertices,
            nativeBakedVertices = nativeBakedVertices,
            radius = radius,
            falloff = falloff,
            strength = strength,
            point1 = point1,
            point2 = point2,
            RendererLocalToWorldMatrix = RendererLocalToWorldMatrix,
            RendererWorldToLocalMatrix = RendererWorldToLocalMatrix,
            RootBoneWorldToLocalMatrix = RootBoneWorldToLocalMatrix,
            nativeBoneWeights = nativeBoneWeights,
            nativeboneMatrices = nativeboneMatrices,
            moveVector = point2 - point1,
            skinned = skinned,
            deformed = deformed,
            selectorType = selectorType,
            sphereA = sphereA,
            sphereB = sphereB,
            axisWeight = axisWeight,
            height = height
        };

        JobHandle jobHandle = deformJob.Schedule(nativeNewVertices.Length, 32);
        jobHandle.Complete();
    }
}

public class Rotator : Deformer
{
    [BurstCompile]
    public struct DeformJob : IJobParallelFor
    {
        public float radius;
        public float falloff;
        public float strength;
        public NativeArray<Vector3> nativeNewVertices;
        public NativeArray<Vector3> nativeBakedVertices;
        public Vector3 point1;
        public Vector3 point2;
        public NativeArray<Matrix4x4> nativeboneMatrices;
        public NativeArray<BoneWeight> nativeBoneWeights;
        public Matrix4x4 RendererWorldToLocalMatrix;
        public Matrix4x4 RendererLocalToWorldMatrix;
        public Matrix4x4 RootBoneWorldToLocalMatrix;
        public Vector3 moveVector;
        public bool skinned;
        public Quaternion rotation;
        public NativeArray<bool> deformed;
        public SelectorType selectorType;
        public Vector3 sphereA;
        public Vector3 sphereB;
        public float height;
        public float axisWeight;

        public void Execute(int index)
        {


            if (selectorType == SelectorType.Sphere)
            {
                float distance = Vector3.Distance(nativeBakedVertices[index], point1);
                if (distance < radius)
                {
                    deformed[0] = true;
                    Vector3 newPoint = point2 + (rotation * (nativeBakedVertices[index] - point2));
                    float factor = 1 - ((distance / radius) * falloff);
                    nativeBakedVertices[index] = Vector3.Lerp(nativeBakedVertices[index], newPoint, strength * factor);
                    nativeNewVertices[index] = BakedToNewVertex(nativeBakedVertices[index], RendererWorldToLocalMatrix, RendererLocalToWorldMatrix, RootBoneWorldToLocalMatrix, nativeboneMatrices, nativeBoneWeights, index, skinned);
                }
            }
            if (selectorType == SelectorType.Capsule)
            {
                Vector3 sphereVector = sphereB - sphereA;
                Vector3 capsuleEndA = sphereA - (radius * sphereVector.normalized);
                Vector3 capsuleEndB = sphereB + (radius * sphereVector.normalized);
                Vector3 capsuleVector = capsuleEndB - capsuleEndA;
                Vector3 p = nativeBakedVertices[index] - capsuleEndA;
                float dot = p.x * capsuleVector.x + p.y * capsuleVector.y + p.z * capsuleVector.z;
                float lengthsq = capsuleVector.sqrMagnitude;
                if ((dot > 0f) && (dot < lengthsq))
                {
                    Vector3 closestPointOnAxis;
                    float f = Vector3.Dot(nativeBakedVertices[index] - sphereA, sphereVector.normalized);
                    if (f < 0)
                    {
                        closestPointOnAxis = sphereA;
                    }
                    else if (f > sphereVector.magnitude)
                    {
                        closestPointOnAxis = sphereB;
                    }
                    else closestPointOnAxis = sphereA + (f * sphereVector.normalized);

                    float axisDistance = Vector3.Distance(closestPointOnAxis, nativeBakedVertices[index]);
                    if (axisDistance < radius)
                    {
                        deformed[0] = true;
                        closestPointOnAxis = sphereA + (f * sphereVector.normalized);
                        axisDistance = Vector3.Distance(closestPointOnAxis, nativeBakedVertices[index]);
                        float axisFactor = 1 - ((axisDistance / radius) * falloff);

                        float centerDistance = Vector3.Distance(point1, nativeBakedVertices[index]);
                        float centerFactor = 1 - ((centerDistance / height) * falloff);

                        float factor = Mathf.Lerp(axisFactor, centerFactor, axisWeight);
                        closestPointOnAxis = Vector3.Lerp(closestPointOnAxis, point1, axisWeight);
                        Vector3 newPoint2 = closestPointOnAxis + (point2 - point1);
                        Vector3 newPoint = newPoint2 + (rotation * (nativeBakedVertices[index] - newPoint2));
                        nativeBakedVertices[index] = Vector3.Lerp(nativeBakedVertices[index], newPoint, strength * factor);
                        nativeNewVertices[index] = BakedToNewVertex(nativeBakedVertices[index], RendererWorldToLocalMatrix, RendererLocalToWorldMatrix, RootBoneWorldToLocalMatrix, nativeboneMatrices, nativeBoneWeights, index, skinned);
                    }
                }
            }
        }
    }

    public override void Deform(NativeArray<Vector3> nativeNewVertices, NativeArray<Vector3> nativeBakedVertices, NativeArray<Matrix4x4> nativeboneMatrices, NativeArray<BoneWeight> nativeBoneWeights, Matrix4x4 RendererLocalToWorldMatrix, Matrix4x4 RendererWorldToLocalMatrix, Matrix4x4 RootBoneWorldToLocalMatrix, bool skinned, NativeArray<bool> deformed, NativeArray<Vector3> nativeNormals)
    {
        SetDeformParams();
        SetPoints(RendererWorldToLocalMatrix, skinned);

        DeformJob deformJob = new DeformJob
        {
            nativeNewVertices = nativeNewVertices,
            nativeBakedVertices = nativeBakedVertices,
            radius = radius,
            falloff = falloff,
            strength = strength,
            point1 = point1,
            point2 = point2,
            RendererLocalToWorldMatrix = RendererLocalToWorldMatrix,
            RendererWorldToLocalMatrix = RendererWorldToLocalMatrix,
            RootBoneWorldToLocalMatrix = RootBoneWorldToLocalMatrix,
            nativeBoneWeights = nativeBoneWeights,
            nativeboneMatrices = nativeboneMatrices,
            moveVector = point2 - point1,
            skinned = skinned,
            rotation = N2.localRotation,
            deformed = deformed,
            selectorType = selectorType,
            sphereA = sphereA,
            sphereB = sphereB,
            axisWeight = axisWeight,
            height = height
        };

        JobHandle jobHandle = deformJob.Schedule(nativeNewVertices.Length, 32);
        jobHandle.Complete();
    }
}
