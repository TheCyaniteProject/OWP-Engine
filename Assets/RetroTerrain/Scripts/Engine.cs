using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Engine : MonoBehaviour
{
    public int worldSize = 4; // number of chunks along each axis
    public int chunkSize = 8; // number of tiles per chunk along each axis
    public float heightScale = 1f;
    public int seed = 0;
    public float noiseScale = 0.1f;
    public bool debugRandomHeights = false; // if true, use random heights instead of Perlin noise (for testing)

    public float terrace = 0.25f; // snap heights to increments of this value (0 for no snapping)

    // When the vertical difference between a tile center and a neighbor exceeds this value,
    // clamp the edge vertices toward the neighbor by at most this amount (per side).
    // Set to 0 to disable clamping.
    public float maxEdgeDelta = 0.5f;

    public Material material;

    public GameObject[] tiles;
    public float[,] heights;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        StartCoroutine(Generate());
    }

    IEnumerator Generate() {
        int tilesPerAxis = Mathf.Max(1, worldSize) * Mathf.Max(1, chunkSize);
        heights = new float[tilesPerAxis, tilesPerAxis];

        for (int x = 0; x < tilesPerAxis; x++) {
            for (int z = 0; z < tilesPerAxis; z++) {
                float sample = debugRandomHeights ? Random.Range(0f, heightScale) : Mathf.PerlinNoise((x + seed) * noiseScale, (z + seed) * noiseScale) * heightScale;
                if (terrace > 0f) {
                    sample = Mathf.Round(sample / terrace) * terrace;
                }
                heights[x, z] = sample;
            }
        }

        for (int cx = 0; cx < worldSize; cx++) {
            for (int cz = 0; cz < worldSize; cz++) {
                CreateChunk(cx, cz);
                // Yield once per chunk to spread work across frames
                yield return null;
            }
        }
    }

    void CreateChunk(int chunkX, int chunkZ) {
        int baseX = chunkX * chunkSize;
        int baseZ = chunkZ * chunkSize;
        int maxX = heights.GetLength(0);
        int maxZ = heights.GetLength(1);

        bool InBounds(int xi, int zi) { return xi >= 0 && xi < maxX && zi >= 0 && zi < maxZ; }
        float GetH(int xi, int zi) {
            if (InBounds(xi, zi)) return heights[xi, zi];
            int cx = Mathf.Clamp(xi, 0, maxX - 1);
            int cz = Mathf.Clamp(zi, 0, maxZ - 1);
            return heights[cx, cz];
        }

        float Avg(params float[] vals) {
            float sum = 0f; int count = 0;
            for (int i = 0; i < vals.Length; i++) { sum += vals[i]; count++; }
            return count > 0 ? sum / count : 0f;
        }

        float ClampEdgeVal(float hFrom, float hTo, float defaultVal) {
            if (maxEdgeDelta <= 0f) return defaultVal;
            float diff = hTo - hFrom;
            if (Mathf.Abs(diff) > maxEdgeDelta) {
                return hFrom + Mathf.Sign(diff) * maxEdgeDelta;
            }
            return defaultVal;
        }

        float ClampBetween(float val, float a, float b) {
            float lo = Mathf.Min(a, b);
            float hi = Mathf.Max(a, b);
            return Mathf.Clamp(val, lo, hi);
        }

        List<Vector3> vertices = new List<Vector3>(chunkSize * chunkSize * 9);
        List<Vector2> uvs = new List<Vector2>(chunkSize * chunkSize * 9);
        List<int> triangles = new List<int>(chunkSize * chunkSize * 8 * 3);

        for (int tz = 0; tz < chunkSize; tz++) {
            for (int tx = 0; tx < chunkSize; tx++) {
                int x = baseX + tx;
                int z = baseZ + tz;

                float hC = GetH(x, z);
                float hN = GetH(x, z + 1);
                float hS = GetH(x, z - 1);
                float hE = GetH(x + 1, z);
                float hW = GetH(x - 1, z);
                float hNE = GetH(x + 1, z + 1);
                float hNW = GetH(x - 1, z + 1);
                float hSE = GetH(x + 1, z - 1);
                float hSW = GetH(x - 1, z - 1);

                // Base (unclamped) heights for the 9 vertices of this tile
                float v4 = hC;                         // center (0.5, 0.5)
                float v1 = Avg(hC, hS);                // south edge middle (0.5, 0)
                float v7 = Avg(hC, hN);                // north edge middle (0.5, 1)
                float v3 = Avg(hC, hW);                // west edge middle  (0, 0.5)
                float v5 = Avg(hC, hE);                // east edge middle  (1, 0.5)
                float v0 = Avg(hC, hW, hS, hSW);       // south-west corner (0, 0)
                float v2 = Avg(hC, hE, hS, hSE);       // south-east corner (1, 0)
                float v6 = Avg(hC, hW, hN, hNW);       // north-west corner (0, 1)
                float v8 = Avg(hC, hE, hN, hNE);       // north-east corner (1, 1)

                // Apply edge clamping per direction; duplicate vertices per-tile prevents stitching.
                if (maxEdgeDelta > 0f) {
                    float diffS = hS - hC; bool cliffS = Mathf.Abs(diffS) > maxEdgeDelta; float signS = Mathf.Sign(diffS);
                    float diffN = hN - hC; bool cliffN = Mathf.Abs(diffN) > maxEdgeDelta; float signN = Mathf.Sign(diffN);
                    float diffE = hE - hC; bool cliffE = Mathf.Abs(diffE) > maxEdgeDelta; float signE = Mathf.Sign(diffE);
                    float diffW = hW - hC; bool cliffW = Mathf.Abs(diffW) > maxEdgeDelta; float signW = Mathf.Sign(diffW);

                    // Edge midpoints clamp directly toward neighbor by at most maxEdgeDelta
                    v1 = ClampEdgeVal(hC, hS, v1);
                    v7 = ClampEdgeVal(hC, hN, v7);
                    v5 = ClampEdgeVal(hC, hE, v5);
                    v3 = ClampEdgeVal(hC, hW, v3);

                    // Corner clamping rules:
                    // - If neither adjacent edge is a cliff: keep averaged corner (no clamp).
                    // - If exactly one adjacent edge is a cliff: clamp corner between center and that edge midpoint.
                    // - If both adjacent edges are cliffs in the same direction: allow up to 2x maxEdgeDelta from center in that direction.
                    // - If both adjacent edges are cliffs in opposite directions: clamp corner between the two edge midpoints.

                    void ClampCornerSingle(ref float vc, float center, float midA, float midB, bool cliffA, bool cliffB) {
                        if (!cliffA && !cliffB) return; // keep averaged
                        if (cliffA && cliffB) {
                            float dirA = Mathf.Sign(midA - center);
                            float dirB = Mathf.Sign(midB - center);
                            if (dirA == dirB && dirA != 0f) {
                                // Same direction: allow up to 2x from center toward that direction
                                float min = center;
                                float max = center;
                                if (dirA > 0f) max = center + 2f * maxEdgeDelta; else min = center - 2f * maxEdgeDelta;
                                vc = Mathf.Clamp(vc, min, max);
                            } else {
                                // Opposite or mixed directions: stay within the band defined by the two midpoints
                                float lo = Mathf.Min(midA, midB);
                                float hi = Mathf.Max(midA, midB);
                                vc = Mathf.Clamp(vc, lo, hi);
                            }
                        } else {
                            // Exactly one cliff: constrain between center and the cliff-side midpoint
                            float edgeMid = cliffA ? midA : midB;
                            float lo = Mathf.Min(center, edgeMid);
                            float hi = Mathf.Max(center, edgeMid);
                            vc = Mathf.Clamp(vc, lo, hi);
                        }
                    }

                    // SW (v0) adjacent to S (v1) and W (v3)
                    ClampCornerSingle(ref v0, hC, v1, v3, cliffS, cliffW);
                    // SE (v2) adjacent to S (v1) and E (v5)
                    ClampCornerSingle(ref v2, hC, v1, v5, cliffS, cliffE);
                    // NW (v6) adjacent to N (v7) and W (v3)
                    ClampCornerSingle(ref v6, hC, v7, v3, cliffN, cliffW);
                    // NE (v8) adjacent to N (v7) and E (v5)
                    ClampCornerSingle(ref v8, hC, v7, v5, cliffN, cliffE);
                }

                int vertStart = vertices.Count;

                // Local positions within the tile, offset by (tx, tz)
                vertices.Add(new Vector3(tx + 0f,   v0, tz + 0f));   // 0
                vertices.Add(new Vector3(tx + 0.5f, v1, tz + 0f));   // 1
                vertices.Add(new Vector3(tx + 1f,   v2, tz + 0f));   // 2
                vertices.Add(new Vector3(tx + 0f,   v3, tz + 0.5f)); // 3
                vertices.Add(new Vector3(tx + 0.5f, v4, tz + 0.5f)); // 4 center
                vertices.Add(new Vector3(tx + 1f,   v5, tz + 0.5f)); // 5
                vertices.Add(new Vector3(tx + 0f,   v6, tz + 1f));   // 6
                vertices.Add(new Vector3(tx + 0.5f, v7, tz + 1f));   // 7
                vertices.Add(new Vector3(tx + 1f,   v8, tz + 1f));   // 8

                uvs.Add(new Vector2(0f, 0f));
                uvs.Add(new Vector2(0.5f, 0f));
                uvs.Add(new Vector2(1f, 0f));
                uvs.Add(new Vector2(0f, 0.5f));
                uvs.Add(new Vector2(0.5f, 0.5f));
                uvs.Add(new Vector2(1f, 0.5f));
                uvs.Add(new Vector2(0f, 1f));
                uvs.Add(new Vector2(0.5f, 1f));
                uvs.Add(new Vector2(1f, 1f));

                // Triangles per tile (same pattern as CreateTile)
                triangles.Add(vertStart + 0); triangles.Add(vertStart + 3); triangles.Add(vertStart + 1);
                triangles.Add(vertStart + 1); triangles.Add(vertStart + 3); triangles.Add(vertStart + 4);

                triangles.Add(vertStart + 1); triangles.Add(vertStart + 5); triangles.Add(vertStart + 2);
                triangles.Add(vertStart + 1); triangles.Add(vertStart + 4); triangles.Add(vertStart + 5);

                triangles.Add(vertStart + 3); triangles.Add(vertStart + 6); triangles.Add(vertStart + 7);
                triangles.Add(vertStart + 3); triangles.Add(vertStart + 7); triangles.Add(vertStart + 4);

                triangles.Add(vertStart + 5); triangles.Add(vertStart + 7); triangles.Add(vertStart + 8);
                triangles.Add(vertStart + 5); triangles.Add(vertStart + 4); triangles.Add(vertStart + 7);
            }
        }

        Mesh mesh = new Mesh();
        mesh.indexFormat = (vertices.Count) > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
        mesh.SetVertices(vertices);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        GameObject chunk = new GameObject($"Chunk_{chunkX}_{chunkZ}");
        chunk.transform.position = new Vector3(baseX, 0f, baseZ);
        chunk.transform.parent = transform;
        MeshFilter mf = chunk.AddComponent<MeshFilter>();
        mf.mesh = mesh;
        MeshRenderer mr = chunk.AddComponent<MeshRenderer>();
        mr.material = material;

        if (tiles == null) tiles = new GameObject[0];
        System.Array.Resize(ref tiles, tiles.Length + 1);
        tiles[tiles.Length - 1] = chunk;
    }


    void CreateTile(Vector3 position, float height) {
        int x = Mathf.RoundToInt(position.x);
        int z = Mathf.RoundToInt(position.z);

        float hC = height; // center (mid) height for this tile

        bool Has(int xi, int zi) {
            if (heights == null) return false;
            int maxX = heights.GetLength(0);
            int maxZ = heights.GetLength(1);
            return xi >= 0 && xi < maxX && zi >= 0 && zi < maxZ;
        }

        float H(int xi, int zi) {
            return Has(xi, zi) ? heights[xi, zi] : hC;
        }

        float hN = H(x, z + 1);
        float hS = H(x, z - 1);
        float hE = H(x + 1, z);
        float hW = H(x - 1, z);
        float hNE = H(x + 1, z + 1);
        float hNW = H(x - 1, z + 1);
        float hSE = H(x + 1, z - 1);
        float hSW = H(x - 1, z - 1);

        float Avg(params float[] vals) {
            float sum = 0f; int count = 0;
            for (int i = 0; i < vals.Length; i++) { sum += vals[i]; count++; }
            return count > 0 ? sum / count : hC;
        }

        // Heights for 9 vertices (local positions: see order below)
        float v4 = hC;                         // center (0.5, 0.5)
        float v1 = Avg(hC, hS);                // south edge middle (0.5, 0)
        float v7 = Avg(hC, hN);                // north edge middle (0.5, 1)
        float v3 = Avg(hC, hW);                // west edge middle  (0, 0.5)
        float v5 = Avg(hC, hE);                // east edge middle  (1, 0.5)
        float v0 = Avg(hC, hW, hS, hSW);       // south-west corner (0, 0)
        float v2 = Avg(hC, hE, hS, hSE);       // south-east corner (1, 0)
        float v6 = Avg(hC, hW, hN, hNW);       // north-west corner (0, 1)
        float v8 = Avg(hC, hE, hN, hNE);       // north-east corner (1, 1)

        Mesh mesh = new Mesh();
        mesh.vertices = new Vector3[]
        {
            new Vector3(0f,    v0, 0f),    // 0
            new Vector3(0.5f,  v1, 0f),    // 1
            new Vector3(1f,    v2, 0f),    // 2
            new Vector3(0f,    v3, 0.5f),  // 3
            new Vector3(0.5f,  v4, 0.5f),  // 4 (center)
            new Vector3(1f,    v5, 0.5f),  // 5
            new Vector3(0f,    v6, 1f),    // 6
            new Vector3(0.5f,  v7, 1f),    // 7
            new Vector3(1f,    v8, 1f)     // 8
        };

        mesh.uv = new Vector2[]
        {
            new Vector2(0f, 0f),
            new Vector2(0.5f, 0f),
            new Vector2(1f, 0f),
            new Vector2(0f, 0.5f),
            new Vector2(0.5f, 0.5f),
            new Vector2(1f, 0.5f),
            new Vector2(0f, 1f),
            new Vector2(0.5f, 1f),
            new Vector2(1f, 1f)
        };

        mesh.triangles = new int[]
        {
            0, 3, 1, 1, 3, 4,
            1, 5, 2, 1, 4, 5,
            3, 6, 7, 3, 7, 4,
            5, 7, 8, 5, 4, 7
        };

        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        GameObject tile = new GameObject("Tile");
        tile.transform.position = position;
        tile.transform.parent = transform;
        MeshFilter meshFilter = tile.AddComponent<MeshFilter>();
        meshFilter.mesh = mesh;
        MeshRenderer meshRenderer = tile.AddComponent<MeshRenderer>();
        meshRenderer.material = material;
        if (tiles == null) tiles = new GameObject[0];
        System.Array.Resize(ref tiles, tiles.Length + 1);
        tiles[tiles.Length - 1] = tile;
    }
}
