using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Threading;
using CielaSpike;
using UnityEditor;
using System.Net;
using Newtonsoft.Json.Linq;
using System.IO;

public class IFCImporterv2 : MonoBehaviour
{

    public string baseUrl = "http://localhost:1234";
    public int projectId = 1;

    Material initMaterial;

    public bool useNamesInsteadOfTypes = false;

    // every shape is a child mesh of the parent object
    Dictionary<string, List<Mesh>> childMeshes = new Dictionary<string, List<Mesh>>();
    GameObject go;

    //TODO: support geo referenced projects
    double Latitude = 0;
    double Longitude = 0;
    double Elevation = 0;
    public double[] GetLatLonEle()
    {
        return new double[] { Latitude, Longitude, Elevation };
    }

    /* delegates */
    public delegate void CallbackEventHandler(GameObject go);
    public event CallbackEventHandler ImportFinished;

    /* public editor assignable variables */
    public MaterialAssignment MaterialAssignment;

    private Dictionary<string, Material> classToMat = new Dictionary<string, Material>();
    private Dictionary<Mesh, string> meshToIfcType;
    private enum Facing { Up, Forward, Right };

    // Use this for initialization
    public void Init()
    {
        meshToIfcType = new Dictionary<Mesh, string>();
        initMaterial = Resources.Load("IFCDefault", typeof(Material)) as Material;

        /* prepare material assignment */
        if (MaterialAssignment != null)
        {
            for (int i = 0; i < MaterialAssignment.MaterialDB.Length; ++i)
            {
                IFCMaterialAssoc mas = MaterialAssignment.MaterialDB[i];
                classToMat.Add(mas.IFCClass, mas.Material);
            }
        }
    }

    public void ImportFile(string path, string name, bool useNamesInsteadOfTypes)
    {
        //this.useNamesInsteadOfTypes = useNamesInsteadOfTypes;
        this.StartCoroutineAsync(Import(baseUrl, projectId));
    }

    //IEnumerator Import(string host, int projectid)
    IEnumerator Import(string host, int projectid)
    {
        string name = null;
        float projectScale = 1f;
        //string file = path;

        yield return Ninja.JumpToUnity;

        //Initiate the session
        string res = RestCall(baseUrl + "/api/projects");

        var jo = JObject.Parse(res);
        name = jo[projectid.ToString()]["ProjectName"].Value<string>();
        Debug.Log("Found project: " + name);

        // abort if already imported
        if (GameObject.Find(name) != null)
        {
            Debug.Log("GameObject already exists, aborting import!");
            ImportFinished(null);
        }
        else
        {
            //not already imported, continue
            Debug.Log("New GameObject, continuing...");

            yield return Ninja.JumpBack;
            res = RestCall(baseUrl + "/api/xbim/load?projectid=" + projectid.ToString());

            jo = JObject.Parse(res);
            if (jo["status"].Value<string>().Equals("success"))
            {
                int id = jo["session"].Value<int>();

                //get the SI Length Unit to determine scale
                string lenRes = RestCall(baseUrl + "/api/xbim/siLengthUnit?sessionid=" + id.ToString());
                var lenJo = JObject.Parse(lenRes);

                if (lenJo["status"].Value<string>().Equals("success"))
                {
                    string unit = lenJo["unit"].Value<string>();
                    Debug.Log("Project file is measured in " + unit);
                    if (unit.Equals("MILLIMETRE"))
                    {
                        projectScale = 1 / 1000.0f;
                    }
                    else if (unit.Equals("CENTIMETRE"))
                    {
                        projectScale = 1 / 100.0f;
                    }
                    else if (unit.Equals("DECIMETRE"))
                    {
                        projectScale = 1 / 10.0f;
                    }
                }

                res = RestCall(baseUrl + "/api/xbim/shapes?sessionid=" + id.ToString());

                var shJo = JArray.Parse(res);
                Debug.Log("Found " + shJo.Count.ToString() + " Elements with shapes...");

                yield return Ninja.JumpToUnity;
                //create parent GameObject
                go = new GameObject();
                go.name = name;
                yield return Ninja.JumpBack;

                for (var i = 0; i < shJo.Count; i++)
                {
                    var item = shJo[i];

                    string iName = item["attributes"]["Name"].Value<string>();
                    string iType = item["attributes"]["IfcType"].Value<string>();
                    string iGlobalId = item["attributes"]["GlobalId"].Value<string>();

                    Debug.Log("Iterating " + iName);

                    //name the mesh
                    string mName = iType;
                    if (useNamesInsteadOfTypes)
                    {
                        if (iName != null && iName.Trim().Length > 0)
                        {
                            mName = iName;
                        }
                    }

                    JArray iShapes = (JArray)item["shapes"];
                    Debug.Log("Found " + iShapes.Count.ToString() + " shapes for item");

                    if (iShapes.Count == 0)
                    {
                        continue;
                    }

                    List<Mesh> shapeMeshes = new List<Mesh>();

                    foreach (var shape in iShapes)
                    {
                        yield return Ninja.JumpToUnity;
                        Mesh m = new Mesh();
                        m.name = mName;
                        meshToIfcType.Add(m, iType);
                        yield return Ninja.JumpBack;

                        //Debug.Log("Iterating shape");
                        //unity can only use per-vertex normal and uv, so we will get problem with shared vertices. Rawvertices contains the XBim vertices, while vertices will double reused ones
                        List<Vector3> rawVertices = new List<Vector3>();
                        List<Vector3> vertices = new List<Vector3>();
                        List<int> indices = new List<int>();

                        //get all vertices
                        for (int j = 0; j < ((JArray)shape["Vertices"]).Count; j++)
                        {
                            //Debug.Log("Found " + ((JArray)shape["Vertices"]).Count.ToString() + " vertices");
                            JArray iVerts = (JArray)shape["Vertices"][j];
                            // y and z is flipped
                            Vector3 vec = new Vector3(iVerts[0].Value<float>() * projectScale, iVerts[1].Value<float>() * projectScale, iVerts[2].Value<float>() * projectScale);
                            rawVertices.Add(vec);
                        }

                        // normal and uv arrays in length of vertices
                        List<Vector3> normals = new List<Vector3>();
                        List<Vector2> uvs = new List<Vector2>();

                        // read normals
                        for (int j = 0; j < ((JArray)shape["Faces"]).Count; j++)
                        {
                            JObject iFace = (JObject)shape["Faces"][j];
                            bool isPlanar = iFace["IsPlanar"].Value<bool>();

                            //one UV per normal
                            List<Vector3> facNormals = new List<Vector3>();

                            //same normal for all vertices
                            if (isPlanar)
                            {
                                Vector3 vec = new Vector3(
                                    iFace["Normals"][0]["Normal"]["X"].Value<float>(), iFace["Normals"][0]["Normal"]["Y"].Value<float>(), iFace["Normals"][0]["Normal"]["Z"].Value<float>()
                                    );
                                vec *= iFace["Normals"][0]["Normal"]["Length"].Value<float>();
                                //vec *= -1f;
                                facNormals.Add(vec);
                            }
                            else //independant normal per vertex
                            {
                                for (int k = 0; k < ((JArray)iFace["Normals"]).Count; k++)
                                {
                                    JObject normal = (JObject)iFace["Normals"][k];
                                    Vector3 vec = new Vector3(
                                        normal["X"].Value<float>(), normal["Y"].Value<float>(), normal["Z"].Value<float>()
                                    );
                                    vec *= normal["Length"].Value<float>();
                                    //vec *= -1f;
                                    facNormals.Add(vec);
                                }
                            }

                            //iterate over indices, honor per-vertex normals
                            JArray facIdx = (JArray)iFace["Indices"];

                            //indices in triplets - we need to REINDEX EVERYTHING...gosh
                            for (int k = 0; k < facIdx.Count; k = k + 3)
                            {
                                int i0 = facIdx[k].Value<int>();
                                int i1 = facIdx[k + 1].Value<int>();
                                int i2 = facIdx[k + 2].Value<int>();

                                Vector3 v0 = rawVertices[i0];
                                Vector3 v1 = rawVertices[i1];
                                Vector3 v2 = rawVertices[i2];

                                // add vertices to vertices 
                                int new_i0 = vertices.Count;
                                vertices.Add(v0);
                                int new_i1 = vertices.Count;
                                vertices.Add(v1);
                                int new_i2 = vertices.Count;
                                vertices.Add(v2);

                                // add indices to indices list
                                indices.Add(new_i0);
                                indices.Add(new_i1);
                                indices.Add(new_i2);

                                float scaleFactor = 0.5f;


                                if (isPlanar)
                                {
                                    // add the one normal and one calculated UV to all newly created vertices
                                    normals.Add(facNormals[0]);
                                    normals.Add(facNormals[0]);
                                    normals.Add(facNormals[0]);

                                    // generate UVs, because the supplied ones are shit?
                                    Quaternion rotation = Quaternion.Inverse(Quaternion.LookRotation(facNormals[0]));
                                    uvs.Add((Vector2)(rotation * v0) * scaleFactor);
                                    uvs.Add((Vector2)(rotation * v1) * scaleFactor);
                                    uvs.Add((Vector2)(rotation * v2) * scaleFactor);
                                }
                                else
                                {
                                    // REALLY? Check that!
                                    // Future helpsterTee: Checked it, works.
                                    normals.Add(facNormals[i0]);
                                    normals.Add(facNormals[i1]);
                                    normals.Add(facNormals[i2]);

                                    // generate UVs, because the supplied ones are shit?
                                    Quaternion r1 = Quaternion.Inverse(Quaternion.LookRotation(facNormals[i0]));
                                    Quaternion r2 = Quaternion.Inverse(Quaternion.LookRotation(facNormals[i1]));
                                    Quaternion r3 = Quaternion.Inverse(Quaternion.LookRotation(facNormals[i2]));
                                    uvs.Add((Vector2)(r1 * v0) * scaleFactor);
                                    uvs.Add((Vector2)(r2 * v1) * scaleFactor);
                                    uvs.Add((Vector2)(r3 * v2) * scaleFactor);
                                }
                            }
                        }

                        // set the mesh's vertices, indices, normals and uvs
                        yield return Ninja.JumpToUnity;
                        m.SetVertices(vertices);
                        m.SetIndices(indices.ToArray(), MeshTopology.Triangles, 0, true);
                        m.SetNormals(normals);
                        m.SetUVs(0, uvs);

                        // recalculate the bounds and tangents
                        m.RecalculateBounds();
                        m.RecalculateTangents();

                        // add to mesh list
                        shapeMeshes.Add(m);
                    }
                    childMeshes.Add(iGlobalId, shapeMeshes);
                }

                yield return Ninja.JumpToUnity;

                // now we'll start creating the game objects in the Unity thread, as that's faster than doing while iterating over the data
                int cnt = 0;
                foreach (string iGlobalId in childMeshes.Keys)
                {
                    List<Mesh> meshes = null;
                    childMeshes.TryGetValue(iGlobalId, out meshes);

                    GameObject ifcItem = new GameObject(iGlobalId);
                    ifcItem.transform.parent = go.transform;

                    foreach (Mesh m in meshes)
                    {
                        //Debug.Log("Iterating mesh...");

                        Material mat = initMaterial;
                        string meshType = null;

                        meshToIfcType.TryGetValue(m, out meshType);

                        //check if materials assigned, otherwise use init mat
                        if (classToMat.ContainsKey(meshType))
                        {
                            classToMat.TryGetValue(meshType, out mat);
                        }

                        GameObject child = new GameObject(m.name);
                        child.transform.parent = ifcItem.transform;

                        // we need to transform and scale, as OpenCascade has another coordinate system
                        child.transform.Rotate(new Vector3(1, 0, 0), -90f);
                        child.transform.localScale = new Vector3(child.transform.localScale.x, child.transform.localScale.y * -1, child.transform.localScale.z);

                        MeshFilter meshFilter = (MeshFilter)child.AddComponent(typeof(MeshFilter));
                        meshFilter.mesh = m;
                        MeshRenderer renderer = child.AddComponent(typeof(MeshRenderer)) as MeshRenderer;

                        renderer.material = mat;
                        child.AddComponent(typeof(SerializeMesh));

                        yield return null;

                        cnt++;
                    }
                }

                ImportFinished(go);
            }
        }
        yield return Ninja.JumpToUnity;
    }

    private string RestCall(string url)
    {
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
        HttpWebResponse response = (HttpWebResponse)request.GetResponse();
        StreamReader reader = new StreamReader(response.GetResponseStream());
        string jsonResponse = reader.ReadToEnd();
        return jsonResponse;
    }

}
        /*



        float projectScale = 1.0f;

        yield return Ninja.JumpToUnity;
        if (GameObject.Find(name) != null)
        {
            Debug.Log("GameObject already exists, aborting import!");
            yield return null;
        }
        yield return Ninja.JumpBack;

        Debug.Log("Parsing geometry from IFC file");
        yield return null;
        bool result = util.ParseIFCFile(file);

        if (!result)
        {
            Debug.Log("Error parsing IFC File");
            yield return null;
        }
        else
        {
            Debug.Log("Finished parsing geometry");
            if (util.Latitude != 0 && util.Longitude != 0)
            {
                Debug.Log("Found georeference with coordinates:" + util.Latitude.ToString() + "," + util.Longitude.ToString());
                Latitude = util.Latitude;
                Longitude = util.Longitude;
                Elevation = util.Elevation;
            }
            else
            {
                Debug.Log("Found no georeference");
            }

            if (util.SILengthUnit != null)
            {
                Debug.Log("Project file is measured in " + util.SILengthUnit);
                if (util.SILengthUnit.Equals(".MILLI..METRE."))
                {
                    projectScale = 1 / 1000.0f;
                }
                else if (util.SILengthUnit.Equals(".CENTI..METRE."))
                {
                    projectScale = 1 / 100.0f;
                }
                else if (util.SILengthUnit.Equals(".DECI..METRE."))
                {
                    projectScale = 1 / 10.0f;
                }
            }

            yield return null;
        }

        //okay here
        items = util.Geometry;

        // calculate dimensions
        Vector3 min = new Vector3();
        Vector3 max = new Vector3();
        bool InitMinMax = false;
        GetDimensions(util.ModelRoot, ref min, ref max, ref InitMinMax);

        Vector3 center = new Vector3();
        center.x = (max.x + min.x) / 2f;
        center.y = (max.y + min.y) / 2f;
        center.z = (max.z + min.z) / 2f;

        center *= projectScale;

        float size = max.x - min.x;

        if (size < max.y - min.y) size = max.y - min.y;
        if (size < max.z - min.z) size = max.z - min.z;

        yield return Ninja.JumpToUnity;
        go = new GameObject();
        go.name = name;
        yield return Ninja.JumpBack;

        int cnt = 0;
        foreach (IfcItem item in items)
        {
            if (cnt % 50 == 0)
            {
                Debug.Log("Processing mesh " + cnt + " of " + items.Count);
                yield return null;
            }

            yield return Ninja.JumpToUnity;
            Mesh m = new Mesh();
            meshToIfcType.Add(m, item.ifcType);

            if (useNamesInsteadOfTypes)
            {
                if (item.name != null && item.name.Trim().Length > 0)
                {
                    m.name = item.name;
                }
                else
                {
                    m.name = item.ifcType;
                }
            }
            else
            {
                m.name = item.ifcType;
            }

            yield return Ninja.JumpBack;
            List<Vector3> vertices = new List<Vector3>();
            for (int i = 0; i < item.verticesCount; i++)
            {
                Vector3 vec = new Vector3((item.vertices[6 * i + 0]) * projectScale, (item.vertices[6 * i + 2]) * projectScale, (item.vertices[6 * i + 1]) * projectScale);
                vertices.Add(vec);
            }
            Debug.Assert(item.vertices.Length == item.verticesCount * 6);

            yield return Ninja.JumpToUnity;
            m.SetVertices(vertices);
            m.SetIndices(item.indicesForFaces, MeshTopology.Triangles, 0, true);

            // calculate UVs
            float scaleFactor = 0.5f;
            Vector2[] uvs = new Vector2[vertices.Count];
            int len = m.GetIndices(0).Length;
            int_t[] idxs = m.GetIndices(0);
            yield return Ninja.JumpBack;
            for (int i = 0; i < len; i = i + 3)
            {
                Vector3 v1 = vertices[idxs[i + 0]];
                Vector3 v2 = vertices[idxs[i + 1]];
                Vector3 v3 = vertices[idxs[i + 2]];
                Vector3 normal = Vector3.Cross(v3 - v1, v2 - v1);
                Quaternion rotation;
                if (normal == Vector3.zero)
                    rotation = new Quaternion();
                else
                    rotation = Quaternion.Inverse(Quaternion.LookRotation(normal));
                uvs[idxs[i + 0]] = (Vector2)(rotation * v1) * scaleFactor;
                uvs[idxs[i + 1]] = (Vector2)(rotation * v2) * scaleFactor;
                uvs[idxs[i + 2]] = (Vector2)(rotation * v3) * scaleFactor;
            }
            yield return Ninja.JumpToUnity;
            m.SetUVs(0, new List<Vector2>(uvs));
            m.RecalculateNormals();
            yield return Ninja.JumpBack;
            meshes.Add(m);

            cnt++;
        }

        cnt = 0;
        yield return Ninja.JumpToUnity;
        foreach (Mesh m in meshes)
        {
            Material mat = initMaterial;
            String meshType = null;

            meshToIfcType.TryGetValue(m, out meshType);

             check if materials assigned, otherwise use init mat 
            if (classToMat.ContainsKey(meshType))
            {
                classToMat.TryGetValue(meshType, out mat);
            }

            GameObject child = new GameObject(m.name);
            child.transform.parent = go.transform;
            MeshFilter meshFilter = (MeshFilter)child.AddComponent(typeof(MeshFilter));
            meshFilter.mesh = m;
            MeshRenderer renderer = child.AddComponent(typeof(MeshRenderer)) as MeshRenderer;

            renderer.material = mat;

            child.AddComponent(typeof(SerializeMesh));

            cnt++;
            yield return null;
        }

        if (util.TrueNorth != 0)
        {
            go.transform.Rotate(new Vector3(0, 1, 0), (float)util.TrueNorth * -1);
        }

        PrefabUtility.CreatePrefab("Assets/IFCGeneratedGeometry/" + name + ".prefab", go);

        allFinished = true;

        // callback for external
        if (ImportFinished != null)
        {
            ImportFinished(go);
        }

        yield return null;
    }

    #region helper methods

    private void GetDimensions(IfcItem ifcItem, ref Vector3 min, ref Vector3 max, ref bool InitMinMax)
    {
        while (ifcItem != null)
        {
            if (ifcItem.verticesCount != 0)
            {
                if (InitMinMax == false)
                {
                    min.x = ifcItem.vertices[3 * 0 + 0];
                    min.y = ifcItem.vertices[3 * 0 + 2];
                    min.z = ifcItem.vertices[3 * 0 + 1];
                    max = min;

                    InitMinMax = true;
                }

                int_t i = 0;
                while (i < ifcItem.verticesCount)
                {

                    min.x = Math.Min(min.x, ifcItem.vertices[6 * i + 0]);
                    min.y = Math.Min(min.y, ifcItem.vertices[6 * i + 2]);
                    min.z = Math.Min(min.z, ifcItem.vertices[6 * i + 1]);

                    max.x = Math.Max(max.x, ifcItem.vertices[6 * i + 0]);
                    max.y = Math.Max(max.y, ifcItem.vertices[6 * i + 2]);
                    max.z = Math.Max(max.z, ifcItem.vertices[6 * i + 1]);

                    i++;
                }
            }

            GetDimensions(ifcItem.child, ref min, ref max, ref InitMinMax);

            ifcItem = ifcItem.next;
        }
    }

    #endregion*/


