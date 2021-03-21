using AIChara;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using KKAPI;
using KKAPI.Chara;
using System;
using System.Collections.Generic;
using UnityEngine;
using KKAPI.Maker;
using System.Collections;
using ExtensibleSaveFormat;
using MessagePack;

[BepInDependency(KoikatuAPI.GUID, KoikatuAPI.VersionConst)]
[BepInPlugin(GUID, "Deformers", Version)]
public class Deformers : BaseUnityPlugin
{
    public const string GUID = "dainty.deformers";
    public const string Version = "0.21";
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
    List<Vector3> newVertices = new List<Vector3>();
    List<Vector3> bakedVertices = new List<Vector3>();
    Mesh bakedMesh = new Mesh();
    private float lastDeform;
    private bool CR_running = false;
    List<Vector3> origVertices = new List<Vector3>();

    protected override void OnCardBeingSaved(GameMode currentGameMode)
    {
        PluginData deformData = new PluginData();

        foreach (SkinnedMeshRenderer renderer in Renderers)
        {
            if (renderer == null)
            {
                continue;
            }
            if (renderer.sharedMesh == null)
            {
                continue;
            }
            if (!renderer.sharedMesh.isReadable)
            {
                continue;
            }
            List<float[]> savedVertices = new List<float[]>();
            renderer.sharedMesh.GetVertices(newVertices);
            OrigMeshes[renderer.sharedMesh].GetVertices(origVertices);
            for (var j = 0; j < newVertices.Count; j++)
            {
                if (newVertices[j] != origVertices[j])
                {
                    savedVertices.Add(new float[] { j, newVertices[j].x, newVertices[j].y, newVertices[j].z });
                }
            }

            if (savedVertices.Count > 0)
            {
                if (deformData.data.ContainsKey(renderer.sharedMesh.name + newVertices.Count))
                {
                    continue;
                }
                deformData.data.Add(renderer.sharedMesh.name + newVertices.Count, MessagePackSerializer.Serialize(savedVertices, MessagePack.Resolvers.ContractlessStandardResolver.Instance));
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
        StartCoroutine(GetAllRenderers(true));
    }
    public IEnumerator GetAllRenderers(bool reload = false, bool deform = false)
    {
        yield return new WaitForSeconds(0.5f); //renderers/meshes get replaced by plugins at some point, I tried hooking the load manually and setting priority to last, doesn't work. Something async I guess.

        Renderers = transform.GetComponentsInChildren(typeof(SkinnedMeshRenderer), true);
        foreach (SkinnedMeshRenderer renderer in Renderers)
        {
            if (renderer.sharedMesh == null)
            {
                continue;
            }
            if (OrigMeshes.ContainsKey(renderer.sharedMesh))
            {
                continue;
            }
            Mesh copyMesh = Instantiate(renderer.sharedMesh);
            OrigMeshes.Add(copyMesh, renderer.sharedMesh);
            String name = renderer.sharedMesh.name;
            renderer.sharedMesh = copyMesh;
            renderer.sharedMesh.name = name;
            if (!renderer.sharedMesh.isReadable)
            {
                Deformers.Logger.LogWarning("Cannot deform " + renderer.sharedMesh.name + ", not read/write enabled.");
            }
        }

        if (reload)
        {
            PluginData deformData = GetExtendedData();
            if (deformData != null)
            {
                foreach (SkinnedMeshRenderer renderer in Renderers)
                {
                    if (renderer == null)
                    {
                        continue;
                    }
                    if (renderer.sharedMesh == null)
                    {
                        continue;
                    }
                    if (!renderer.sharedMesh.isReadable)
                    {
                        continue;
                    }
                    renderer.sharedMesh.GetVertices(newVertices);
                    if (deformData.data.TryGetValue(renderer.sharedMesh.name + newVertices.Count, out object t))
                    {
                        List<float[]> savedVertices = MessagePackSerializer.Deserialize<List<float[]>>((byte[])t, MessagePack.Resolvers.ContractlessStandardResolver.Instance);
                        for (var i = 0; i < savedVertices.Count; i++)
                        {
                            float[] savedVertex = savedVertices[i];
                            newVertices[(int)savedVertex[0]] = new Vector3(savedVertex[1], savedVertex[2], savedVertex[3]);
                        }

                        renderer.sharedMesh.SetVertices(newVertices);
                        lastDeform = Time.time;
                        if (CR_running == false)
                        {
                            StartCoroutine(RecalculateMeshes());
                        }
                    }
                }
            }
        }

        if (deform)
        {
            StartCoroutine(WaitDeform());
        }
    }

    public void DeformAll()
    {
        if (DeformerList == null || Renderers == null)
        {
            return;
        }

        foreach (SkinnedMeshRenderer renderer in Renderers)
        {
            if (renderer == null)
            {
                continue;
            }
            if (renderer.sharedMesh == null)
            {
                continue;
            }
            if (!OrigMeshes.ContainsKey(renderer.sharedMesh))
            {
                continue;
            }
            if (!renderer.sharedMesh.isReadable)
            {
                continue;
            }
            OrigMeshes[renderer.sharedMesh].GetVertices(newVertices);
            renderer.sharedMesh.SetVertices(newVertices);

            Transform parent = renderer.transform.parent;
            renderer.transform.parent = null;

            Vector3 localScale = renderer.transform.localScale;
            renderer.transform.localScale = Vector3.one;

            Matrix4x4[] boneMatrices = new Matrix4x4[renderer.bones.Length];
            BoneWeight[] boneWeights = renderer.sharedMesh.boneWeights;
            Transform[] skinnedBones = renderer.bones;
            Matrix4x4[] meshBindposes = renderer.sharedMesh.bindposes;

            if (boneWeights.Length > 0)
            {
                renderer.BakeMesh(bakedMesh);
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
            }
            else
            {
                bakedVertices = new List<Vector3>(newVertices);
                Transform rootBone = renderer.rootBone;
                for (int j = 0; j < bakedVertices.Count; j++)
                {
                    bakedVertices[j] = rootBone.TransformPoint(bakedVertices[j]);
                    bakedVertices[j] = renderer.transform.InverseTransformPoint(bakedVertices[j]);
                }
            }

            foreach (Deformer deformer in DeformerList)
            {
                if (deformer.FilterMaterial.shader.name != "Standard")
                {
                    foreach (Material material in renderer.materials)
                    {
                        if (material.shader.name == deformer.FilterMaterial.shader.name)
                        {
                            deformer.Deform(renderer, newVertices, bakedVertices, boneMatrices, boneWeights);
                            break;
                        }
                    }
                }
                else
                {
                    deformer.Deform(renderer, newVertices, bakedVertices, boneMatrices, boneWeights);
                }
            }
            renderer.transform.localScale = localScale;
            renderer.transform.parent = parent;
            renderer.sharedMesh.SetVertices(newVertices);
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

        foreach (SkinnedMeshRenderer renderer in Renderers)
        {
            if (renderer == null)
            {
                continue;
            }
            if (renderer.sharedMesh == null)
            {
                continue;
            }
            if (!renderer.sharedMesh.isReadable)
            {
                continue;
            }
            NormalSolver.RecalculateNormals(renderer.sharedMesh, 50f);
            renderer.sharedMesh.RecalculateTangents();
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
    void LateUpdate()
    {
        if (
            N1.localPosition != oldN1Position || N1.localEulerAngles != oldN1Rotation || N1.localScale != oldN1Scale ||
            N2.localPosition != oldN2Position || N2.localEulerAngles != oldN2Rotation || N2.localScale != oldN2Scale ||
            oldShaderName != FilterMaterial.shader.name
            )
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

        oldN1Position = N1.localPosition;
        oldN1Rotation = N1.localEulerAngles;
        oldN1Scale = N1.localScale;

        oldN2Position = N2.localPosition;
        oldN2Rotation = N2.localEulerAngles;
        oldN2Scale = N2.localScale;

        oldShaderName = FilterMaterial.shader.name;
    }

    public abstract void Deform(Component renderer, List<Vector3> newVertices, List<Vector3> bakedVertices, Matrix4x4[] boneMatrices, BoneWeight[] boneWeights);

    public Matrix4x4 GetReverseSkinningMatrix(Matrix4x4[] boneMatrices, BoneWeight weight)
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

    public Vector3 BakedToNewVertex(Vector3 bakedVertex, Component renderer, Matrix4x4[] boneMatrices, BoneWeight[] boneWeights, int index)
    {
        Vector3 newVertex;
        if (boneWeights.Length > 0)
        {
            BoneWeight weight = boneWeights[index];
            Matrix4x4 reverseSkinningMatrix = GetReverseSkinningMatrix(boneMatrices, weight);
            newVertex = reverseSkinningMatrix.MultiplyPoint3x4(renderer.transform.TransformPoint(bakedVertex));
        }
        else
        {
            Vector3 v = renderer.transform.TransformPoint(bakedVertex);
            newVertex = ((SkinnedMeshRenderer)renderer).rootBone.InverseTransformPoint(v);
        }
        return newVertex;
    }
}

public class Squeezer : Deformer
{
    public float radius = 1.09f;
    public float falloff = 1;
    public float strength = 0.2f;
    public override void Deform(Component renderer, List<Vector3> newVertices, List<Vector3> bakedVertices, Matrix4x4[] boneMatrices, BoneWeight[] boneWeights)
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

        Vector3 point1 = renderer.transform.InverseTransformPoint(N1.position);
        Vector3 point2 = renderer.transform.InverseTransformPoint(N2.position);

        for (var j = 0; j < bakedVertices.Count; j++)
        {
            float distance = Vector3.Distance(bakedVertices[j], point1);
            if (distance < radius)
            {
                float factor = 1 - ((distance / radius) * falloff);
                bakedVertices[j] = Vector3.Lerp(bakedVertices[j], point2, strength * factor);
                newVertices[j] = BakedToNewVertex(bakedVertices[j], renderer, boneMatrices, boneWeights, j);
            }
        }
    }

}

public class Bulger : Deformer
{
    public float radius = 1.09f;
    public float falloff = 1;
    public float strength = 0.2f;
    public override void Deform(Component renderer, List<Vector3> newVertices, List<Vector3> bakedVertices, Matrix4x4[] boneMatrices, BoneWeight[] boneWeights)
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

        Vector3 point1 = renderer.transform.InverseTransformPoint(N1.position);
        Vector3 point2 = renderer.transform.InverseTransformPoint(N2.position);

        for (var j = 0; j < bakedVertices.Count; j++)
        {
            float distance = Vector3.Distance(bakedVertices[j], point1);
            if (distance < radius)
            {
                Vector3 newPoint = bakedVertices[j] + (bakedVertices[j] - point2);
                float factor = 1 - ((distance / radius) * falloff);
                bakedVertices[j] = Vector3.Lerp(bakedVertices[j], newPoint, strength * factor);
                newVertices[j] = BakedToNewVertex(bakedVertices[j], renderer, boneMatrices, boneWeights, j);
            }
        }
    }
}

public class Mover : Deformer
{
    public float radius = 1.09f;
    public float falloff = 1;
    public float strength = 0.2f;
    public override void Deform(Component renderer, List<Vector3> newVertices, List<Vector3> bakedVertices, Matrix4x4[] boneMatrices, BoneWeight[] boneWeights)
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

        Vector3 point1 = renderer.transform.InverseTransformPoint(N1.position);
        Vector3 point2 = renderer.transform.InverseTransformPoint(N2.position);
        Vector3 moveVector = point2 - point1;
        float factor;
        for (var j = 0; j < bakedVertices.Count; j++)
        {
            float distance = Vector3.Distance(bakedVertices[j], point1);
            if (distance < radius)
            {
                Vector3 newPoint = bakedVertices[j] + moveVector;
                factor = 1 - ((distance / radius) * falloff);
                bakedVertices[j] = Vector3.Lerp(bakedVertices[j], newPoint, strength * factor);
                newVertices[j] = BakedToNewVertex(bakedVertices[j], renderer, boneMatrices, boneWeights, j);
            }
        }
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