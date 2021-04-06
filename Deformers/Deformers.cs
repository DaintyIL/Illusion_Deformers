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
    public const string Version = "0.4";
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
    private float lastDeform;
    private bool CR_running = false;
    private bool loaded = false;
    private float loadedTime = 0f;

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
                        lastDeform = Time.time;
                        if (CR_running == false)
                        {
                            StartCoroutine(RecalculateMeshes());
                        }
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

            OrigMeshes[sharedMesh].GetVertices(newVertices);
            sharedMesh.SetVertices(newVertices);

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
            NativeArray<Vector3> nativeNewVertices = new NativeArray<Vector3>(newVertices.ToArray(), Allocator.TempJob);
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
                    nativeboneMatrices = new NativeArray<Matrix4x4>(boneMatrices, Allocator.TempJob);
                    nativeBoneWeights = new NativeArray<BoneWeight>(boneWeights, Allocator.TempJob);
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

            NativeArray<Vector3> nativeBakedVertices = new NativeArray<Vector3>(bakedVertices.ToArray(), Allocator.TempJob);

            foreach (Deformer deformer in DeformerList)
            {
                if (!deformer.gameObject.activeInHierarchy)
                {
                    continue;
                }
                if (deformer.FilterMaterial.shader.name != "Standard")
                {
                    foreach (Material material in ((Renderer)renderer).materials)
                    {
                        if (material.shader.name == deformer.FilterMaterial.shader.name)
                        {
                            deformer.Deform(nativeNewVertices, nativeBakedVertices, nativeboneMatrices, nativeBoneWeights, rendererTransform.localToWorldMatrix, rendererTransform.worldToLocalMatrix, rootBoneWorldToLocalMatrix, skinned);
                            break;
                        }
                    }
                }
                else
                {
                    deformer.Deform(nativeNewVertices, nativeBakedVertices, nativeboneMatrices, nativeBoneWeights, rendererTransform.localToWorldMatrix, rendererTransform.worldToLocalMatrix, rootBoneWorldToLocalMatrix, skinned);
                }
            }

            newVertices.Clear();
            newVertices.AddRange(nativeNewVertices);
            nativeNewVertices.Dispose();
            nativeBakedVertices.Dispose();
            nativeBoneWeights.Dispose();
            nativeboneMatrices.Dispose();

            rendererTransform.localScale = localScale;
            rendererTransform.parent = parent;
            sharedMesh.SetVertices(newVertices);
        }
        lastDeform = Time.time;
        if (CR_running == false)
        {
            StartCoroutine(RecalculateMeshes());
        }
    }

    private IEnumerator RecalculateMeshes()
    {
        CR_running = true;
        yield return new WaitUntil(() => (Time.time - lastDeform) >= 1);

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
            NormalSolver.RecalculateNormals(sharedMesh, 50f);
            sharedMesh.RecalculateTangents();
        }

        CR_running = false;
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
    public Vector3 point1;
    public Vector3 point2;

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

    void LateUpdate()
    {
        if (N1.localScale != oldN1Scale)
        {
            //keep N1 scale proportional, it's a sphere.
            Vector3[,] addMove = deformersController.GetComponent<ChaControl>().nowCoordinate.accessory.parts[AccessoryIndex].addMove;
            addMove[0, 2] = new Vector3(N1.localScale.x, N1.localScale.x, N1.localScale.x);
            deformersController.GetComponent<ChaControl>().nowCoordinate.accessory.parts[AccessoryIndex].addMove = addMove;
            N1.localScale = new Vector3(N1.localScale.x, N1.localScale.x, N1.localScale.x);
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

    public abstract void Deform(NativeArray<Vector3> nativeNewVertices, NativeArray<Vector3> nativeBakedVertices, NativeArray<Matrix4x4> nativeBoneMatrices, NativeArray<BoneWeight> nativeBoneWeights, Matrix4x4 RendererLocalToWorldMatrix, Matrix4x4 RendererWorldToLocalMatrix, Matrix4x4 RootBoneWorldToLocalMatrix, bool skinned);

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
        falloff = N2.localScale.y;
        if (falloff == 0.01f)
        {
            falloff = 0f;
        }
        if (strength == 0.01f)
        {
            strength = 0f;
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

        public void Execute(int index)
        {
            float distance = Vector3.Distance(nativeBakedVertices[index], point1);
            if (distance < radius)
            {
                float factor = 1 - ((distance / radius) * falloff);
                nativeBakedVertices[index] = Vector3.Lerp(nativeBakedVertices[index], point2, strength * factor);
                nativeNewVertices[index] = BakedToNewVertex(nativeBakedVertices[index], RendererWorldToLocalMatrix, RendererLocalToWorldMatrix, RootBoneWorldToLocalMatrix, nativeboneMatrices, nativeBoneWeights, index, skinned);
            }
        }
    }

    public override void Deform(NativeArray<Vector3> nativeNewVertices, NativeArray<Vector3> nativeBakedVertices, NativeArray<Matrix4x4> nativeboneMatrices, NativeArray<BoneWeight> nativeBoneWeights, Matrix4x4 RendererLocalToWorldMatrix, Matrix4x4 RendererWorldToLocalMatrix, Matrix4x4 RootBoneWorldToLocalMatrix, bool skinned)
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
            skinned = skinned
        };

        JobHandle jobHandle = deformJob.Schedule(nativeNewVertices.Length, 32);
        JobHandle.ScheduleBatchedJobs();
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

        public void Execute(int index)
        {
            float distance = Vector3.Distance(nativeBakedVertices[index], point1);
            if (distance < radius)
            {
                Vector3 newPoint = nativeBakedVertices[index] + (nativeBakedVertices[index] - point2);
                float factor = 1 - ((distance / radius) * falloff);
                nativeBakedVertices[index] = Vector3.Lerp(nativeBakedVertices[index], newPoint, strength * factor);
                nativeNewVertices[index] = BakedToNewVertex(nativeBakedVertices[index], RendererWorldToLocalMatrix, RendererLocalToWorldMatrix, RootBoneWorldToLocalMatrix, nativeboneMatrices, nativeBoneWeights, index, skinned);
            }
        }
    }

    public override void Deform(NativeArray<Vector3> nativeNewVertices, NativeArray<Vector3> nativeBakedVertices, NativeArray<Matrix4x4> nativeboneMatrices, NativeArray<BoneWeight> nativeBoneWeights, Matrix4x4 RendererLocalToWorldMatrix, Matrix4x4 RendererWorldToLocalMatrix, Matrix4x4 RootBoneWorldToLocalMatrix, bool skinned)
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
            skinned = skinned
        };

        JobHandle jobHandle = deformJob.Schedule(nativeNewVertices.Length, 32);
        JobHandle.ScheduleBatchedJobs();
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

        public void Execute(int index)
        {
            float distance = Vector3.Distance(nativeBakedVertices[index], point1);
            if (distance < radius)
            {
                Vector3 newPoint = nativeBakedVertices[index] + moveVector;
                float factor = 1 - ((distance / radius) * falloff);
                nativeBakedVertices[index] = Vector3.Lerp(nativeBakedVertices[index], newPoint, strength * factor);
                nativeNewVertices[index] = BakedToNewVertex(nativeBakedVertices[index], RendererWorldToLocalMatrix, RendererLocalToWorldMatrix, RootBoneWorldToLocalMatrix, nativeboneMatrices, nativeBoneWeights, index, skinned);
            }
        }
    }

    public override void Deform(NativeArray<Vector3> nativeNewVertices, NativeArray<Vector3> nativeBakedVertices, NativeArray<Matrix4x4> nativeboneMatrices, NativeArray<BoneWeight> nativeBoneWeights, Matrix4x4 RendererLocalToWorldMatrix, Matrix4x4 RendererWorldToLocalMatrix, Matrix4x4 RootBoneWorldToLocalMatrix, bool skinned)
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
            skinned = skinned
        };

        JobHandle jobHandle = deformJob.Schedule(nativeNewVertices.Length, 32);
        JobHandle.ScheduleBatchedJobs();
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

        public void Execute(int index)
        {
            float distance = Vector3.Distance(nativeBakedVertices[index], point1);
            if (distance < radius)
            {
                Vector3 newPoint = point2 + (rotation * (nativeBakedVertices[index] - point2));
                float factor = 1 - ((distance / radius) * falloff);
                nativeBakedVertices[index] = Vector3.Lerp(nativeBakedVertices[index], newPoint, strength * factor);
                nativeNewVertices[index] = BakedToNewVertex(nativeBakedVertices[index], RendererWorldToLocalMatrix, RendererLocalToWorldMatrix, RootBoneWorldToLocalMatrix, nativeboneMatrices, nativeBoneWeights, index, skinned);
            }
        }
    }

    public override void Deform(NativeArray<Vector3> nativeNewVertices, NativeArray<Vector3> nativeBakedVertices, NativeArray<Matrix4x4> nativeboneMatrices, NativeArray<BoneWeight> nativeBoneWeights, Matrix4x4 RendererLocalToWorldMatrix, Matrix4x4 RendererWorldToLocalMatrix, Matrix4x4 RootBoneWorldToLocalMatrix, bool skinned)
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
            rotation = N2.localRotation
        };

        JobHandle jobHandle = deformJob.Schedule(nativeNewVertices.Length, 32);
        JobHandle.ScheduleBatchedJobs();
        jobHandle.Complete();
    }
}

/*
 * The following code was taken from: http://schemingdeveloper.com
 *
 * Visit our game studio website: http://stopthegnomes.com
 *
 * License: You may use this code however you see fit, as long as you include this notice
 *          without any modifications.
 *
 *          You may not publish a paid asset on Unity store if its main function is based on
 *          the following code, but you may publish a paid asset that uses this code.
 *
 *          If you intend to use this in a Unity store asset or a commercial project, it would
 *          be appreciated, but not required, if you let me know with a link to the asset. If I
 *          don't get back to you just go ahead and use it anyway!
 *          http://schemingdeveloper.com/2017/03/26/better-method-recalculate-normals-unity-part-2/
 */

public static class NormalSolver
{
    /// <summary>
    ///     Recalculate the normals of a mesh based on an angle threshold. This takes
    ///     into account distinct vertices that have the same position.
    /// </summary>
    /// <param name="mesh"></param>
    /// <param name="angle">
    ///     The smoothing angle. Note that triangles that already share
    ///     the same vertex will be smooth regardless of the angle!
    /// </param>
    public static void RecalculateNormals(this Mesh mesh, float angle)
    {
        var cosineThreshold = Mathf.Cos(angle * Mathf.Deg2Rad);

        var vertices = mesh.vertices;
        var normals = new Vector3[vertices.Length];

        // Holds the normal of each triangle in each sub mesh.
        var triNormals = new Vector3[mesh.subMeshCount][];

        var dictionary = new Dictionary<VertexKey, List<VertexEntry>>(vertices.Length);

        for (var subMeshIndex = 0; subMeshIndex < mesh.subMeshCount; ++subMeshIndex)
        {

            var triangles = mesh.GetTriangles(subMeshIndex);

            triNormals[subMeshIndex] = new Vector3[triangles.Length / 3];

            for (var i = 0; i < triangles.Length; i += 3)
            {
                int i1 = triangles[i];
                int i2 = triangles[i + 1];
                int i3 = triangles[i + 2];

                // Calculate the normal of the triangle
                Vector3 p1 = vertices[i2] - vertices[i1];
                Vector3 p2 = vertices[i3] - vertices[i1];
                Vector3 normal = Vector3.Cross(p1, p2);
                float magnitude = normal.magnitude;
                if (magnitude > 0) normal /= magnitude;
                int triIndex = i / 3;
                triNormals[subMeshIndex][triIndex] = normal;

                List<VertexEntry> entry;
                VertexKey key;

                if (!dictionary.TryGetValue(key = new VertexKey(vertices[i1]), out entry))
                {
                    entry = new List<VertexEntry>(4);
                    dictionary.Add(key, entry);
                }
                entry.Add(new VertexEntry(subMeshIndex, triIndex, i1));

                if (!dictionary.TryGetValue(key = new VertexKey(vertices[i2]), out entry))
                {
                    entry = new List<VertexEntry>();
                    dictionary.Add(key, entry);
                }
                entry.Add(new VertexEntry(subMeshIndex, triIndex, i2));

                if (!dictionary.TryGetValue(key = new VertexKey(vertices[i3]), out entry))
                {
                    entry = new List<VertexEntry>();
                    dictionary.Add(key, entry);
                }
                entry.Add(new VertexEntry(subMeshIndex, triIndex, i3));
            }
        }

        // Each entry in the dictionary represents a unique vertex position.

        foreach (var vertList in dictionary.Values)
        {
            for (var i = 0; i < vertList.Count; ++i)
            {

                var sum = new Vector3();
                var lhsEntry = vertList[i];

                for (var j = 0; j < vertList.Count; ++j)
                {
                    var rhsEntry = vertList[j];

                    if (lhsEntry.VertexIndex == rhsEntry.VertexIndex)
                    {
                        sum += triNormals[rhsEntry.MeshIndex][rhsEntry.TriangleIndex];
                    }
                    else
                    {
                        // The dot product is the cosine of the angle between the two triangles.
                        // A larger cosine means a smaller angle.
                        var dot = Vector3.Dot(
                            triNormals[lhsEntry.MeshIndex][lhsEntry.TriangleIndex],
                            triNormals[rhsEntry.MeshIndex][rhsEntry.TriangleIndex]);
                        if (dot >= cosineThreshold)
                        {
                            sum += triNormals[rhsEntry.MeshIndex][rhsEntry.TriangleIndex];
                        }
                    }
                }

                normals[lhsEntry.VertexIndex] = sum.normalized;
            }
        }

        mesh.normals = normals;
    }

    private struct VertexKey
    {
        private readonly long _x;
        private readonly long _y;
        private readonly long _z;

        // Change this if you require a different precision.
        private const int Tolerance = 100000;

        // Magic FNV values. Do not change these.
        private const long FNV32Init = 0x811c9dc5;
        private const long FNV32Prime = 0x01000193;

        public VertexKey(Vector3 position)
        {
            _x = (long)(Mathf.Round(position.x * Tolerance));
            _y = (long)(Mathf.Round(position.y * Tolerance));
            _z = (long)(Mathf.Round(position.z * Tolerance));
        }

        public override bool Equals(object obj)
        {
            var key = (VertexKey)obj;
            return _x == key._x && _y == key._y && _z == key._z;
        }

        public override int GetHashCode()
        {
            long rv = FNV32Init;
            rv ^= _x;
            rv *= FNV32Prime;
            rv ^= _y;
            rv *= FNV32Prime;
            rv ^= _z;
            rv *= FNV32Prime;

            return rv.GetHashCode();
        }
    }

    private struct VertexEntry
    {
        public int MeshIndex;
        public int TriangleIndex;
        public int VertexIndex;

        public VertexEntry(int meshIndex, int triIndex, int vertIndex)
        {
            MeshIndex = meshIndex;
            TriangleIndex = triIndex;
            VertexIndex = vertIndex;
        }
    }
}
