﻿using UnityEngine;
using UnityEditor;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SplineMesh {
    /// <summary>
    /// A component that create a deformed mesh from a given one, according to a cubic Bézier curve and other parameters.
    /// The mesh will always be bended along the X axis. Extreme X coordinates of source mesh verticies will be used as a bounding to the deformed mesh.
    /// The resulting mesh is stored in a MeshFilter component and automaticaly updated each time the cubic Bézier curve control points are changed.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MeshFilter))]
    [ExecuteInEditMode]
    public class MeshBender : MonoBehaviour {
        private bool isDirty = false;
        private Mesh result;
        private bool useSpline;
        private Spline spline;
        private float intervalStart, intervalEnd;
        private CubicBezierCurve curve;
        private Dictionary<float, CurveSample> sampleCache = new Dictionary<float, CurveSample>();

        private SourceMesh source;
        /// <summary>
        /// The source mesh to bend.
        /// </summary>
        public SourceMesh Source {
            get { return source; }
            set {
                if (value == source) return;
                isDirty = true;
                source = value;

                var m = source.Mesh;
                result.hideFlags = m.hideFlags;
                result.indexFormat = m.indexFormat;
                result.vertices = m.vertices.ToArray();

                result.uv = m.uv.ToArray();
                result.uv2 = m.uv2.ToArray();
                result.uv3 = m.uv3.ToArray();
                result.uv4 = m.uv4.ToArray();
                result.uv5 = m.uv5.ToArray();
                result.uv6 = m.uv6.ToArray();
                result.uv7 = m.uv7.ToArray();
                result.uv8 = m.uv8.ToArray();
                result.tangents = m.tangents.ToArray();

                result.triangles = source.Triangles;
            }
        }
        
        private FillingMode mode = FillingMode.StretchToInterval;
        /// <summary>
        /// The scale to apply to the source mesh before bending it.
        /// Scale on X axis is internaly limited to -1;1 to restrain the mesh inside the curve bounds.
        /// </summary>
        public FillingMode Mode {
            get { return mode; }
            set {
                if (value == mode) return;
                isDirty = true;
                mode = value;
            }
        }

        public void SetInterval(CubicBezierCurve curve) {
            if (this.curve == curve) return;
            if (curve == null) throw new ArgumentNullException("curve");
            if (this.curve != null) {
                this.curve.Changed.RemoveListener(Compute);
            }
            this.curve = curve;
            curve.Changed.AddListener(Compute);
            useSpline = false;
            isDirty = true;
        }

        public void SetInterval(Spline spline, float intervalStart, float intervalEnd = 0) {
            if (spline == null) throw new ArgumentNullException("spline");
            if (intervalStart < 0 || intervalStart >= spline.Length) {
                throw new ArgumentOutOfRangeException("interval start must be 0or greater and lesser than spline length (was " + intervalStart + ")");
            }
            this.spline = spline;
            this.intervalStart = intervalStart;
            this.intervalEnd = intervalEnd;
            useSpline = true;
            isDirty = true;
        }


        private void OnEnable() {
            if(GetComponent<MeshFilter>().sharedMesh != null) {
                result = GetComponent<MeshFilter>().sharedMesh;
            } else {
                GetComponent<MeshFilter>().sharedMesh = result = new Mesh();
                result.name = "Generated by " + GetType().Name;
            }
        }

        /// <summary>
        /// Bend the mesh only if a property has changed since the last compute.
        /// </summary>
        public void ComputeIfNeeded() {
            if (!isDirty) return;
            Compute();
        }

        /// <summary>
        /// Bend the mesh. This method may take time and should not be called more than necessary.
        /// Consider using <see cref="ComputeIfNeeded"/> for faster result.
        /// </summary>
        public void Compute() {
            isDirty = false;
            switch (Mode) {
                case FillingMode.Once:
                    FillOnce();
                    break;
                case FillingMode.Repeat:
                    FillRepeat();
                    break;
                case FillingMode.StretchToInterval:
                    FillStretch();
                    break;
            }
        }

        private void OnDestroy() {
            if(curve != null) {
                curve.Changed.RemoveListener(Compute);
            }
        }

        public enum FillingMode {
            Once,
            Repeat,
            StretchToInterval
        }

        private void FillOnce() {
            sampleCache.Clear();
            var bentVertices = new List<MeshVertex>(source.Vertices.Count);
            // for each mesh vertex, we found its projection on the curve
            foreach (var vert in source.Vertices) {
                float distance = vert.position.x - source.MinX;
                CurveSample sample;
                if (!sampleCache.TryGetValue(distance, out sample)) {
                    if (!useSpline) {
                        if (distance > curve.Length) continue;
                        sample = curve.GetSampleAtDistance(distance);
                    } else {
                        float distOnSpline = intervalStart + distance;
                        if (true) { //spline.isLoop) {
                            while (distOnSpline > spline.Length) {
                                distOnSpline -= spline.Length;
                            }
                        } else if (distOnSpline > spline.Length) {
                            continue;
                        }
                        sample = spline.GetSampleAtDistance(distOnSpline);
                    }
                    sampleCache[distance] = sample;
                }

                bentVertices.Add(sample.GetBent(vert));
            }

            result.vertices = bentVertices.Select(b => b.position).ToArray();
            result.normals = bentVertices.Select(b => b.normal).ToArray();
            result.RecalculateBounds();
        }

        private void FillRepeat() {
            float intervalLength;
            if (!useSpline) {
                intervalLength = curve.Length;
            } else {
                intervalLength = (intervalEnd == 0 ? spline.Length : intervalEnd) - intervalStart;
            }
            var repetitionCount = Mathf.FloorToInt(intervalLength / source.Length);
            var bentVertices = new List<MeshVertex>(source.Vertices.Count);
            var triangles = new List<int>();
            var uv = new List<Vector2>();
            var uv2 = new List<Vector2>();
            var tangents = new List<Vector4>();
            float offset = 0;
            for (int i = 0; i < repetitionCount; i++) {
                foreach(var index in source.Triangles) {
                    triangles.Add(index + source.Vertices.Count * i);
                }
                uv.AddRange(source.Mesh.uv);
                uv2.AddRange(source.Mesh.uv2);
                tangents.AddRange(source.Mesh.tangents);

                sampleCache.Clear();
                // for each mesh vertex, we found its projection on the curve
                foreach (var vert in source.Vertices) {
                    float distance = vert.position.x - source.MinX + offset;
                    CurveSample sample;
                    if (!sampleCache.TryGetValue(distance, out sample)) {
                        if (!useSpline) {
                            if (distance > curve.Length) continue;
                            sample = curve.GetSampleAtDistance(distance);
                        } else {
                            float distOnSpline = intervalStart + distance;
                            if (true) { //spline.isLoop) {
                                while (distOnSpline > spline.Length) {
                                    distOnSpline -= spline.Length;
                                }
                            } else if (distOnSpline > spline.Length) {
                                continue;
                            }
                            sample = spline.GetSampleAtDistance(distOnSpline);
                        }
                        sampleCache[distance] = sample;
                    }
                    bentVertices.Add(sample.GetBent(vert));
                }
                offset += source.Length;
            }

            result.triangles = triangles.ToArray();
            result.vertices = bentVertices.Select(b => b.position).ToArray();
            result.normals = bentVertices.Select(b => b.normal).ToArray();
            result.uv = uv.ToArray();
            result.uv2 = uv2.ToArray();
            result.tangents = tangents.ToArray();

            result.RecalculateBounds();
        }

        private void FillStretch() {
            var bentVertices = new List<MeshVertex>(source.Vertices.Count);
            sampleCache.Clear();
            // for each mesh vertex, we found its projection on the curve
            foreach (var vert in source.Vertices) {
                float distanceRate = source.Length == 0 ? 0 : Math.Abs(vert.position.x - source.MinX) / source.Length;
                CurveSample sample;
                if (!sampleCache.TryGetValue(distanceRate, out sample)) {
                    if (!useSpline) {
                        sample = curve.GetSampleAtDistance(curve.Length * distanceRate);
                    } else {
                        float intervalLength = intervalEnd == 0 ? spline.Length - intervalStart : intervalEnd - intervalStart;
                        float distOnSpline = intervalStart + intervalLength * distanceRate;
                        if(distOnSpline > spline.Length) {
                            distOnSpline = spline.Length;
                            Debug.Log("dist " + distOnSpline + " spline length " + spline.Length + " start " + intervalStart);
                        }

                        sample = spline.GetSampleAtDistance(distOnSpline);
                    }
                    sampleCache[distanceRate] = sample;
                }

                bentVertices.Add(sample.GetBent(vert));
            }

            result.vertices = bentVertices.Select(b => b.position).ToArray();
            result.normals = bentVertices.Select(b => b.normal).ToArray();
            result.RecalculateBounds();
        }
    }
}