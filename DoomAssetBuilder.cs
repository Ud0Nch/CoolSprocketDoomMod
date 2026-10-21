using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SprocketDoomMod;

// йоу йоу йоу картошка freeee
internal static class DoomAssetBuilder
{
    internal readonly struct SpanEntry
    {
        public readonly ushort Y;
        public readonly ushort X0;
        public readonly ushort X1;
        public readonly byte R;
        public readonly byte G;
        public readonly byte B;

        public SpanEntry(ushort y, ushort x0, ushort x1, byte r, byte g, byte b)
        {
            Y = y;
            X0 = x0;
            X1 = x1;
            R = r;
            G = g;
            B = b;
        }
    }

    internal static List<SpanEntry[]> ReadSpansBin(byte[] bytes, out int width, out int height)
    {
        var data = bytes.AsSpan();
        if (data.Length < 10 ||
            data[0] != (byte)'D' ||
            data[1] != (byte)'O' ||
            data[2] != (byte)'O' ||
            data[3] != (byte)'M')
        {
            throw new InvalidDataException("Invalid spans.bin header.");
        }

        int frameCount = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(4, 2));
        width = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(6, 2));
        height = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(8, 2));

        int offset = 10;
        var result = new List<SpanEntry[]>(frameCount);

        for (int f = 0; f < frameCount; f++)
        {
            int spanCount = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
            offset += 2;

            var spans = new SpanEntry[spanCount];
            for (int s = 0; s < spanCount; s++)
            {
                ushort y = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
                ushort x0 = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset + 2, 2));
                ushort x1 = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset + 4, 2));
                byte r = data[offset + 6];
                byte g = data[offset + 7];
                byte b = data[offset + 8];
                offset += 9;

                spans[s] = new SpanEntry(y, x0, x1, r, g, b);
            }

            result.Add(spans);
        }

        return result;
    }

    internal static Mesh BuildCutoutMesh(SpanEntry[] spans, int width, int height, string name)
    {
        int n = spans.Length;
        var vertices = new Vector3[n * 4];
        var uvs = new Vector2[n * 4];
        var normals = new Vector3[n * 4];
        var colors = new Color[n * 4];
        var triangles = new int[n * 12];

        float invW = 1.0f / Math.Max(1, width);
        float invH = 1.0f / Math.Max(1, height);

        for (int i = 0; i < n; i++)
        {
            SpanEntry sp = spans[i];

            float uLeft = sp.X0 * invW;
            float uRight = sp.X1 * invW;
            float vTop = 1.0f - sp.Y * invH;
            float vBot = 1.0f - (sp.Y + 1) * invH;

            float xLeft = uLeft - 0.5f;
            float xRight = uRight - 0.5f;
            float yBot = vBot;
            float yTop = vTop;

            int vIdx = i * 4;
            vertices[vIdx + 0] = new Vector3(xLeft, yBot, 0f);
            vertices[vIdx + 1] = new Vector3(xLeft, yTop, 0f);
            vertices[vIdx + 2] = new Vector3(xRight, yTop, 0f);
            vertices[vIdx + 3] = new Vector3(xRight, yBot, 0f);

            uvs[vIdx + 0] = new Vector2(uLeft, vBot);
            uvs[vIdx + 1] = new Vector2(uLeft, vTop);
            uvs[vIdx + 2] = new Vector2(uRight, vTop);
            uvs[vIdx + 3] = new Vector2(uRight, vBot);

            normals[vIdx + 0] = Vector3.back;
            normals[vIdx + 1] = Vector3.back;
            normals[vIdx + 2] = Vector3.back;
            normals[vIdx + 3] = Vector3.back;

            var col = new Color(sp.R / 255f, sp.G / 255f, sp.B / 255f, 1f);
            colors[vIdx + 0] = col;
            colors[vIdx + 1] = col;
            colors[vIdx + 2] = col;
            colors[vIdx + 3] = col;

            int tIdx = i * 12;

            // лицевая сторона
            triangles[tIdx + 0] = vIdx + 0;
            triangles[tIdx + 1] = vIdx + 1;
            triangles[tIdx + 2] = vIdx + 2;
            triangles[tIdx + 3] = vIdx + 0;
            triangles[tIdx + 4] = vIdx + 2;
            triangles[tIdx + 5] = vIdx + 3;

            // обратная сторона
            triangles[tIdx + 6] = vIdx + 0;
            triangles[tIdx + 7] = vIdx + 2;
            triangles[tIdx + 8] = vIdx + 1;
            triangles[tIdx + 9] = vIdx + 0;
            triangles[tIdx + 10] = vIdx + 3;
            triangles[tIdx + 11] = vIdx + 2;
        }

        var mesh = new Mesh
        {
            name = name,
            hideFlags = HideFlags.HideAndDontSave
        };
        mesh.vertices = vertices;
        mesh.uv = uvs;
        mesh.normals = normals;
        mesh.colors = colors;
        mesh.triangles = triangles;
        mesh.RecalculateBounds();
        return mesh;
    }

    internal static SpanEntry[] BuildDefaultFallbackSpans(int width, int height)
    {
        int x0 = (int)(width * 0.34f);
        int x1 = (int)(width * 0.66f);
        int yStart = (int)(height * 0.58f);
        var list = new List<SpanEntry>(height - yStart);
        for (int y = yStart; y < height; y++)
        {
            list.Add(new SpanEntry((ushort)y, (ushort)x0, (ushort)x1, 70, 75, 85));
        }
        return list.ToArray();
    }

    internal static Mesh BuildWireframePolyhedronMesh(int shapeType, float thickness = 0.038f)
    {
        Vector3[] verts;
        int[,] edges;

        switch (shapeType)
        {
            case 0: // октаэдр
                verts = new[]
                {
                    new Vector3( 0f,  1.25f,  0f),
                    new Vector3( 0f, -1.25f,  0f),
                    new Vector3( 1f,  0f,     0f),
                    new Vector3(-1f,  0f,     0f),
                    new Vector3( 0f,  0f,     1f),
                    new Vector3( 0f,  0f,    -1f),
                };
                edges = new[,]
                {
                    {0,2}, {0,3}, {0,4}, {0,5},
                    {1,2}, {1,3}, {1,4}, {1,5},
                    {2,4}, {4,3}, {3,5}, {5,2}
                };
                break;

            case 1: // куб
                const float c = 0.82f;
                verts = new[]
                {
                    new Vector3(-c, -c, -c),
                    new Vector3( c, -c, -c),
                    new Vector3( c,  c, -c),
                    new Vector3(-c,  c, -c),
                    new Vector3(-c, -c,  c),
                    new Vector3( c, -c,  c),
                    new Vector3( c,  c,  c),
                    new Vector3(-c,  c,  c),
                };
                edges = new[,]
                {
                    {0,1}, {1,2}, {2,3}, {3,0},
                    {4,5}, {5,6}, {6,7}, {7,4},
                    {0,4}, {1,5}, {2,6}, {3,7}
                };
                break;

            case 2: // звезда Меркаба
                const float t = 0.88f;
                verts = new[]
                {
                    new Vector3( t,  t,  t),
                    new Vector3( t, -t, -t),
                    new Vector3(-t,  t, -t),
                    new Vector3(-t, -t,  t),

                    new Vector3(-t, -t, -t),
                    new Vector3(-t,  t,  t),
                    new Vector3( t, -t,  t),
                    new Vector3( t,  t, -t),
                };
                edges = new[,]
                {
                    {0,1}, {0,2}, {0,3}, {1,2}, {2,3}, {3,1},
                    {4,5}, {4,6}, {4,7}, {5,6}, {6,7}, {7,5}
                };
                break;

            case 4: // ореол
                verts = new Vector3[11];
                for (int i = 0; i < 8; i++)
                {
                    float ang = i * Mathf.PI / 4f;
                    verts[i] = new Vector3(Mathf.Cos(ang) * 0.95f, 0.35f, Mathf.Sin(ang) * 0.95f);
                }
                verts[8]  = new Vector3(-0.55f, 0.95f, 0f);
                verts[9]  = new Vector3( 0.55f, 0.95f, 0f);
                verts[10] = new Vector3( 0.00f, 0.15f, 0f);
                edges = new[,]
                {
                    {0,1}, {1,2}, {2,3}, {3,4}, {4,5}, {5,6}, {6,7}, {7,0},
                    {8,9}, {8,10}, {9,10}
                };
                break;

            case 5: // маяк зоны задания (пока не работает)
                verts = new Vector3[18];
                for (int i = 0; i < 8; i++)
                {
                    float ang = i * Mathf.PI / 4f;
                    verts[i] = new Vector3(Mathf.Cos(ang) * 4.5f, 0.35f, Mathf.Sin(ang) * 4.5f);
                    verts[8 + i] = new Vector3(Mathf.Cos(ang) * 1.4f, 18.0f, Mathf.Sin(ang) * 1.4f);
                }
                verts[16] = new Vector3(0f, 0.2f, 0f);
                verts[17] = new Vector3(0f, 24.0f, 0f);
                edges = new[,]
                {
                    // нижнее кольцо
                    {0,1}, {1,2}, {2,3}, {3,4}, {4,5}, {5,6}, {6,7}, {7,0},
                    // верхнее кольцо
                    {8,9}, {9,10}, {10,11}, {11,12}, {12,13}, {13,14}, {14,15}, {15,8},
                    // стойки и лучики к вершине
                    {0,8}, {2,10}, {4,12}, {6,14},
                    {8,17}, {10,17}, {12,17}, {14,17},
                    {16,17}
                };
                break;

            default: // кристалл, гексагональная бипирамида
                verts = new Vector3[8];
                verts[0] = new Vector3(0f, 1.35f, 0f);
                verts[1] = new Vector3(0f, -1.35f, 0f);
                for (int i = 0; i < 6; i++)
                {
                    float ang = i * Mathf.PI / 3f;
                    verts[2 + i] = new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang));
                }
                edges = new[,]
                {
                    {0,2}, {0,3}, {0,4}, {0,5}, {0,6}, {0,7},
                    {1,2}, {1,3}, {1,4}, {1,5}, {1,6}, {1,7},
                    {2,3}, {3,4}, {4,5}, {5,6}, {6,7}, {7,2}
                };
                break;
        }

        int edgeCount = edges.GetLength(0);
        var outVerts = new List<Vector3>(edgeCount * 8);
        var outNorms = new List<Vector3>(edgeCount * 8);
        var outUvs = new List<Vector2>(edgeCount * 8);
        var outTris = new List<int>(edgeCount * 24);

        for (int e = 0; e < edgeCount; e++)
        {
            Vector3 a = verts[edges[e, 0]];
            Vector3 b = verts[edges[e, 1]];
            Vector3 dir = (b - a);
            if (dir.sqrMagnitude < 0.0001f)
                continue;
            dir.Normalize();

            Vector3 upRef = Mathf.Abs(dir.y) < 0.9f ? Vector3.up : Vector3.right;
            Vector3 n1 = Vector3.Cross(dir, upRef).normalized * thickness;
            Vector3 n2 = Vector3.Cross(dir, n1).normalized * thickness;

            int baseIdx = outVerts.Count;
            // две перпендикулярные ленты на ребро чтоыб каркас читался с любого ракурса
            outVerts.Add(a - n1);
            outVerts.Add(a + n1);
            outVerts.Add(b + n1);
            outVerts.Add(b - n1);

            outVerts.Add(a - n2);
            outVerts.Add(a + n2);
            outVerts.Add(b + n2);
            outVerts.Add(b - n2);

            for (int k = 0; k < 8; k++)
            {
                outNorms.Add(Vector3.up);
                outUvs.Add(new Vector2(0.5f, 0.5f));
            }

            outTris.Add(baseIdx + 0); outTris.Add(baseIdx + 1); outTris.Add(baseIdx + 2);
            outTris.Add(baseIdx + 0); outTris.Add(baseIdx + 2); outTris.Add(baseIdx + 3);
            outTris.Add(baseIdx + 0); outTris.Add(baseIdx + 2); outTris.Add(baseIdx + 1);
            outTris.Add(baseIdx + 0); outTris.Add(baseIdx + 3); outTris.Add(baseIdx + 2);

            outTris.Add(baseIdx + 4); outTris.Add(baseIdx + 5); outTris.Add(baseIdx + 6);
            outTris.Add(baseIdx + 4); outTris.Add(baseIdx + 6); outTris.Add(baseIdx + 7);
            outTris.Add(baseIdx + 4); outTris.Add(baseIdx + 6); outTris.Add(baseIdx + 5);
            outTris.Add(baseIdx + 4); outTris.Add(baseIdx + 7); outTris.Add(baseIdx + 6);
        }

        var mesh = new Mesh
        {
            name = $"DoomWireframePoly_{shapeType}",
            hideFlags = HideFlags.HideAndDontSave
        };
        mesh.vertices = outVerts.ToArray();
        mesh.normals = outNorms.ToArray();
        mesh.uv = outUvs.ToArray();
        mesh.triangles = outTris.ToArray();
        mesh.RecalculateBounds();
        return mesh;
    }
}
