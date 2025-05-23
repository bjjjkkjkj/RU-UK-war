﻿/*
MIT License

Copyright (c) 2021 Andrzej Więckowski, Ph.D., https://github.com/andywiecko/BurstTriangulator

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

*/
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using andywiecko.BurstTriangulator.LowLevel.Unsafe;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
#if UNITY_MATHEMATICS_FIXEDPOINT
using Unity.Mathematics.FixedPoint;
#endif

[assembly: InternalsVisibleTo("andywiecko.BurstTriangulator.Tests")]

namespace andywiecko.BurstTriangulator
{
    public enum Preprocessor
    {
        None = 0,
        /// <summary>
        /// Transforms <see cref="Input"/> to local coordinate system using <em>center of mass</em>.
        /// </summary>
        COM,
        /// <summary>
        /// Transforms <see cref="Input"/> using coordinate system obtained from <em>principal component analysis</em>.
        /// </summary>
        PCA
    }

    [Serializable]
    public class RefinementThresholds
    {
        /// <summary>
        /// Specifies the maximum area constraint for triangles in the resulting mesh refinement.
        /// Ensures that no triangle in the mesh has an area larger than the specified value.
        /// </summary>
        [field: SerializeField]
        public float Area { get; set; } = 1f;
        /// <summary>
        /// Specifies the refinement angle constraint for triangles in the resulting mesh.
        /// Ensures that no triangle in the mesh has an angle smaller than the specified value.
        /// </summary>
        /// <remarks>
        /// Expressed in <em>radians</em>.
        /// </remarks>
        [field: SerializeField]
        public float Angle { get; set; } = math.radians(5);
    }

    [Serializable]
    public class TriangulationSettings
    {
        /// <summary>
        /// If set to <see langword="true"/>, holes and boundaries will be created automatically
        /// depending on the provided <see cref="InputData{T2}.ConstraintEdges"/>.
        /// </summary>
        /// <remarks>
        /// This implements the <see href="https://en.wikipedia.org/wiki/Even%E2%80%93odd_rule">odd-even fill rule</see>.
        ///
        /// When this mode is used, you should ensure that all constraints form closed loops. If any constraints are part of open chains,
        /// then the result is not well-defined.
        /// </remarks>
        [field: SerializeField]
        public bool AutoHolesAndBoundary { get; set; } = false;
        [field: SerializeField]
        public RefinementThresholds RefinementThresholds { get; } = new();
        /// <summary>
        /// If <see langword="true"/> refines mesh using
        /// <see href="https://en.wikipedia.org/wiki/Delaunay_refinement#Ruppert's_algorithm">Ruppert's algorithm</see>.
        /// </summary>
        [field: SerializeField]
        public bool RefineMesh { get; set; } = false;
        /// <summary>
        /// If set to <see langword="true"/>, the provided <see cref="InputData{T2}"/> and <see cref="TriangulationSettings"/>
        /// will be validated before executing the triangulation procedure. The input <see cref="InputData{T2}.Positions"/>,
        /// <see cref="InputData{T2}.ConstraintEdges"/>, and <see cref="TriangulationSettings"/> have certain restrictions.
        /// For more details, see the <see href="https://andywiecko.github.io/BurstTriangulator/manual/advanced/input-validation.html">manual</see>.
        /// If any of the validation conditions are not met, the triangulation will not be performed.
        /// This can be detected as an error by checking the <see cref="OutputData{T2}.Status"/> value (native, and usable in jobs).
        /// Additionally, if <see cref="Verbose"/> is set to <see langword="true"/>, corresponding errors/warnings will be logged in the Console.
        /// Note that some conditions may result in warnings only.
        /// </summary>
        /// <remarks>
        /// Input validation can be expensive. If you are certain of your input, consider disabling this option for additional performance.
        /// </remarks>
        [field: SerializeField]
        public bool ValidateInput { get; set; } = true;
        /// <summary>
        /// If set to <see langword="true"/>, caught errors and warnings with <see cref="Triangulator"/> will be logged in the Console.
        /// </summary>
        /// <remarks>
        /// See also the <see cref="ValidateInput"/> settings.
        /// </remarks>
        /// <seealso cref="ValidateInput"/>
        [field: SerializeField]
        public bool Verbose { get; set; } = true;
        /// <summary>
        /// If <see langword="true"/> the mesh boundary is restored using <see cref="InputData{T}.ConstraintEdges"/>.
        /// </summary>
        [field: SerializeField]
        public bool RestoreBoundary { get; set; } = false;
        /// <summary>
        /// Max iteration count during Sloan's algorithm (constraining edges).
        /// <b>Modify this only if you know what you are doing.</b>
        /// </summary>
        [field: SerializeField]
        public int SloanMaxIters { get; set; } = 1_000_000;
        /// <summary>
        /// Preprocessing algorithm for the input data. Default is <see cref="Preprocessor.None"/>.
        /// </summary>
        [field: SerializeField]
        public Preprocessor Preprocessor { get; set; } = Preprocessor.None;
    }

    public enum ConstraintType : byte
    {
        /// <summary>
        /// A constrained edge will always be present in the output mesh.
        ///
        /// In rare cases where a vertex lies exactly on the constraint, this exact edge may not be present, but it may have been split up into smaller
        /// edges that together form the original constraint.
        /// </summary>
        Constrained,
        /// <summary>
        /// A hole boundary edge works like a constrained edge, but will additionally define where the holes/boundaries of the mesh are.
        ///
        /// This applies when using the <see cref="TriangulationSettings.AutoHolesAndBoundary"/> or <see cref="TriangulationSettings.RestoreBoundary"/> setting.
        ///
        /// Boundary constraints must form closed loops. If they do not, the result is not well-defined.
        ///
        /// If a boundary constraint overlaps and is collinear with another non-boundary constraint, then the boundary constraint will take precedence.
        /// </summary>
        ConstrainedAndHoleBoundary,
    }

    public enum HalfedgeState : byte
    {
        Unconstrained,
        Constrained,
        ConstrainedAndHoleBoundary,
    }

    public class InputData<T2> where T2 : unmanaged
    {
        /// <summary>
        /// Positions of points used in triangulation.
        /// </summary>
        public NativeArray<T2> Positions { get; set; }
        /// <summary>
        /// Optional buffer for constraint edges. This array constrains specific edges to be included in the final
        /// triangulation result. It should contain indexes corresponding to the <see cref="Positions"/> of the edges
        /// in the format [a₀, a₁, b₀, b₁, c₀, c₁, ...], where (a₀, a₁), (b₀, b₁), (c₀, c₁), etc., represent the constraint edges.
        /// </summary>
        /// <remarks>
        /// <b>Note:</b> If refinement is enabled, the provided constraints may be split during the refinement process.
        /// </remarks>
        public NativeArray<int> ConstraintEdges { get; set; }
        /// <summary>
        /// An array of <see cref="ConstraintType"/> values corresponding to each edge in <see cref="ConstraintEdges"/>.
        ///
        /// If created, the length must be exactly half of the length of <see cref="ConstraintEdges"/>.
        ///
        /// If not set, all constraints will be treated as <see cref="ConstraintType.ConstrainedAndHoleBoundary"/>.
        /// </summary>
        public NativeArray<ConstraintType> ConstraintEdgeTypes { get; set; }
        /// <summary>
        /// Optional buffer containing seeds for holes. These hole seeds serve as starting points for a removal process that
        /// mimics the spread of a virus. During this process, <see cref="ConstraintEdges"/> act as barriers to prevent further propagation.
        /// For more information, refer to the documentation.
        /// </summary>
        public NativeArray<T2> HoleSeeds { get; set; }
    }

    /// <summary>
    /// Allocation free input class with implicit cast to <see cref="InputData{T2}"/>.
    /// </summary>
    /// <exclude />
    [Obsolete("Use AsNativeArray(out Handle) instead! You can learn more in the project manual.")]
    public class ManagedInput<T2> where T2 : unmanaged
    {
        public T2[] Positions { get; set; }
        public int[] ConstraintEdges { get; set; }
        public T2[] HoleSeeds { get; set; }

        public static implicit operator InputData<T2>(ManagedInput<T2> input) => new()
        {
            Positions = input.Positions == null ? default : input.Positions.AsNativeArray(),
            ConstraintEdges = input.ConstraintEdges == null ? default : input.ConstraintEdges.AsNativeArray(),
            HoleSeeds = input.HoleSeeds == null ? default : input.HoleSeeds.AsNativeArray(),
        };
    }

    public class OutputData<T2> where T2 : unmanaged
    {
        /// <summary>
        /// Positions of triangulation points.
        /// </summary>
        /// <remarks>
        /// <b>Note:</b> This buffer may include additional points than <see cref="InputData{T2}.Positions"/> if refinement is enabled.
        /// Additionally, the positions might differ slightly (by a small ε) if a <see cref="TriangulationSettings.Preprocessor"/> is applied.
        /// </remarks>
        public NativeList<T2> Positions => owner.outputPositions;
        /// <summary>
        /// Continuous buffer of resulting triangles. All triangles are guaranteed to be oriented clockwise.
        /// </summary>
        public NativeList<int> Triangles => owner.triangles;
        /// <summary>
        /// Status of the triangulation. Retrieve this value to detect any errors that occurred during triangulation.
        /// </summary>
        public NativeReference<Status> Status => owner.status;
        /// <summary>
        /// Continuous buffer of resulting halfedges. A value of -1 indicates that there is no corresponding opposite halfedge.
        /// For more information, refer to the documentation on halfedges.
        /// </summary>
        public NativeList<int> Halfedges => owner.halfedges;
        /// <summary>
        /// Buffer corresponding to <see cref="Halfedges"/>.
        /// </summary>
        public NativeList<HalfedgeState> ConstrainedHalfedges => owner.constrainedHalfedges;
        private readonly Triangulator<T2> owner;
        [Obsolete("This will be converted into internal ctor.")]
        public OutputData(Triangulator<T2> owner) => this.owner = owner;
    }

    /// <summary>
    /// A handle that prevents an object from being deallocated by the garbage collector (GC).
    /// Call <see cref="Free"/> to release the object.
    /// </summary>
    /// <seealso cref="Extensions.AsNativeArray{T}(T[], out Handle)"/>
    public readonly struct Handle
    {
        private readonly ulong gcHandle;
        /// <summary>
        /// Creates a <see cref="Handle"/>.
        /// </summary>
        /// <param name="gcHandle">The handle value, which can be obtained e.g. from
        /// <see cref="UnsafeUtility.PinGCArrayAndGetDataAddress(Array, out ulong)"/> or
        /// <see cref="UnsafeUtility.PinGCObjectAndGetAddress(object, out ulong)"/>.
        /// </param>
        /// <seealso cref="Extensions.AsNativeArray{T}(T[], out Handle)"/>
        public Handle(ulong gcHandle) => this.gcHandle = gcHandle;
        /// <summary>
        /// Releases the handle, allowing the object to be collected by the garbage collector.
        /// </summary>
        public readonly void Free() => UnsafeUtility.ReleaseGCObject(gcHandle);
    }

    /// <summary>
    /// A wrapper for <see cref="Triangulator{T2}"/> where T2 is <see cref="double2"/>.
    /// </summary>
    /// <seealso cref="Triangulator{T2}"/>
    public class Triangulator : IDisposable
    {
        public TriangulationSettings Settings => impl.Settings;
        public InputData<double2> Input { get => impl.Input; set => impl.Input = value; }
        public OutputData<double2> Output => impl.Output;
        private readonly Triangulator<double2> impl;
        public Triangulator(int capacity, Allocator allocator) => impl = new(capacity, allocator);
        public Triangulator(Allocator allocator) => impl = new(allocator);

        /// <summary>
        /// Releases all resources (memory and safety handles).
        /// </summary>
        public void Dispose() => impl.Dispose();

        /// <summary>
        /// Perform the job's Execute method immediately on the same thread.
        /// </summary>
        public void Run() => impl.Run();

        /// <summary>
        /// Schedule the job for execution on a worker thread.
        /// </summary>
        /// <param name="dependencies">
        /// Dependencies are used to ensure that a job executes on worker threads after the dependency has completed execution.
        /// Making sure that two jobs reading or writing to same data do not run in parallel.
        /// </param>
        /// <returns>
        /// The handle identifying the scheduled job. Can be used as a dependency for a later job or ensure completion on the main thread.
        /// </returns>
        public JobHandle Schedule(JobHandle dependencies = default) => impl.Schedule(dependencies);
    }

    public class Triangulator<T2> : IDisposable where T2 : unmanaged
    {
        public TriangulationSettings Settings { get; } = new();
        public InputData<T2> Input { get; set; } = new();
        public OutputData<T2> Output { get; }

        internal NativeList<T2> outputPositions;
        internal NativeList<int> triangles;
        internal NativeList<int> halfedges;
        internal NativeList<HalfedgeState> constrainedHalfedges;
        internal NativeReference<Status> status;

        public Triangulator(int capacity, Allocator allocator)
        {
            outputPositions = new(capacity, allocator);
            triangles = new(6 * capacity, allocator);
            status = new(Status.Ok, allocator);
            halfedges = new(6 * capacity, allocator);
            constrainedHalfedges = new(6 * capacity, allocator);
#pragma warning disable CS0618
            Output = new(this);
#pragma warning restore CS0618
        }

        public Triangulator(Allocator allocator) : this(capacity: 16 * 1024, allocator) { }

        /// <summary>
        /// Releases all resources (memory and safety handles).
        /// </summary>
        public void Dispose()
        {
            outputPositions.Dispose();
            triangles.Dispose();
            status.Dispose();
            halfedges.Dispose();
            constrainedHalfedges.Dispose();
        }
    }

    public static class Extensions
    {
        /// <summary>
        /// Returns <see cref="NativeArray{T}"/> view on managed <paramref name="array"/>.
        /// </summary>
        /// <typeparam name="T">The type of the elements.</typeparam>
        /// <param name="array">Array to </param>
        /// <returns>View on managed <paramref name="array"/> with <see cref="NativeArray{T}"/>.</returns>
        /// <exclude />
        [Obsolete("Use AsNativeArray(out Handle) instead! You can learn more in the project manual.")]
        unsafe public static NativeArray<T> AsNativeArray<T>(this T[] array) where T : unmanaged
        {
            var ret = default(NativeArray<T>);
            // In Unity 2023.2+ pointers are not required, one can use Span<T> instead.
            fixed (void* ptr = array)
            {
                ret = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<T>(ptr, array.Length, Allocator.None);
            }
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            var m_SafetyHandle = AtomicSafetyHandle.Create();
            NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref ret, m_SafetyHandle);
#endif
            return ret;
        }

        /// <summary>
        /// Returns <see cref="NativeArray{T}"/> view on managed <paramref name="array"/>
        /// with <paramref name="handle"/> to prevents from deallocation.
        /// <para/>
        /// <b>Warning!</b> User has to call <see cref="Handle.Free"/>
        /// manually to release the data for GC! Read more in the project manual.
        /// </summary>
        /// <typeparam name="T">The type of the elements.</typeparam>
        /// <param name="array">Array to view.</param>
        /// <param name="handle">A handle that prevents the <paramref name="array"/> from being deallocated by the GC.</param>
        /// <returns><see cref="NativeArray{T}"/> view on managed <paramref name="array"/> with <see cref="NativeArray{T}"/>.</returns>
        public static unsafe NativeArray<T> AsNativeArray<T>(this T[] array, out Handle handle) where T : unmanaged
        {
            var ptr = UnsafeUtility.PinGCArrayAndGetDataAddress(array, out var gcHandle);
            var ret = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<T>(ptr, array.Length, Allocator.None);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            var m_SafetyHandle = AtomicSafetyHandle.Create();
            NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref ret, m_SafetyHandle);
#endif
            handle = new(gcHandle);
            return ret;
        }

        /// <summary>
        /// Perform the job's Execute method immediately on the same thread.
        /// </summary>
        public static void Run(this Triangulator<float2> @this) =>
        new TriangulationJob<float, float2, float, TransformFloat, FloatUtils>(@this).Run();
        /// <summary>
        /// Schedule the job for execution on a worker thread.
        /// </summary>
        /// <param name="dependencies">
        /// Dependencies are used to ensure that a job executes on worker threads after the dependency has completed execution.
        /// Making sure that two jobs reading or writing to same data do not run in parallel.
        /// </param>
        /// <returns>
        /// The handle identifying the scheduled job. Can be used as a dependency for a later job or ensure completion on the main thread.
        /// </returns>
        public static JobHandle Schedule(this Triangulator<float2> @this, JobHandle dependencies = default) =>
        new TriangulationJob<float, float2, float, TransformFloat, FloatUtils>(@this).Schedule(dependencies);

        /// <summary>
        /// Perform the job's Execute method immediately on the same thread.
        /// </summary>
        public static void Run(this Triangulator<Vector2> @this) =>
        new TriangulationJob<float, float2, float, TransformFloat, FloatUtils>(
            input: new() { Positions = @this.Input.Positions.Reinterpret<float2>(), ConstraintEdges = @this.Input.ConstraintEdges, ConstraintEdgeTypes = @this.Input.ConstraintEdgeTypes, HoleSeeds = @this.Input.HoleSeeds.Reinterpret<float2>() },
            output: new() { Triangles = @this.triangles, Halfedges = @this.halfedges, Positions = UnsafeUtility.As<NativeList<Vector2>, NativeList<float2>>(ref @this.outputPositions), Status = @this.status, ConstrainedHalfedges = @this.constrainedHalfedges },
            args: @this.Settings
            ).Run();
        /// <summary>
        /// Schedule the job for execution on a worker thread.
        /// </summary>
        /// <param name="dependencies">
        /// Dependencies are used to ensure that a job executes on worker threads after the dependency has completed execution.
        /// Making sure that two jobs reading or writing to same data do not run in parallel.
        /// </param>
        /// <returns>
        /// The handle identifying the scheduled job. Can be used as a dependency for a later job or ensure completion on the main thread.
        /// </returns>
        public static JobHandle Schedule(this Triangulator<Vector2> @this, JobHandle dependencies = default) =>
        new TriangulationJob<float, float2, float, TransformFloat, FloatUtils>(
            input: new() { Positions = @this.Input.Positions.Reinterpret<float2>(), ConstraintEdges = @this.Input.ConstraintEdges, ConstraintEdgeTypes = @this.Input.ConstraintEdgeTypes, HoleSeeds = @this.Input.HoleSeeds.Reinterpret<float2>() },
            output: new() { Triangles = @this.triangles, Halfedges = @this.halfedges, Positions = UnsafeUtility.As<NativeList<Vector2>, NativeList<float2>>(ref @this.outputPositions), Status = @this.status, ConstrainedHalfedges = @this.constrainedHalfedges },
            args: @this.Settings
            ).Schedule(dependencies);

        /// <summary>
        /// Perform the job's Execute method immediately on the same thread.
        /// </summary>
        public static void Run(this Triangulator<double2> @this) =>
        new TriangulationJob<double, double2, double, TransformDouble, DoubleUtils>(@this).Run();
        /// <summary>
        /// Schedule the job for execution on a worker thread.
        /// </summary>
        /// <param name="dependencies">
        /// Dependencies are used to ensure that a job executes on worker threads after the dependency has completed execution.
        /// Making sure that two jobs reading or writing to same data do not run in parallel.
        /// </param>
        /// <returns>
        /// The handle identifying the scheduled job. Can be used as a dependency for a later job or ensure completion on the main thread.
        /// </returns>
        public static JobHandle Schedule(this Triangulator<double2> @this, JobHandle dependencies = default) =>
        new TriangulationJob<double, double2, double, TransformDouble, DoubleUtils>(@this).Schedule(dependencies);

        /// <summary>
        /// Perform the job's Execute method immediately on the same thread.
        /// </summary>
        public static void Run(this Triangulator<int2> @this) =>
        new TriangulationJob<int, int2, long, TransformInt, IntUtils>(@this).Run();
        /// <summary>
        /// Schedule the job for execution on a worker thread.
        /// </summary>
        /// <param name="dependencies">
        /// Dependencies are used to ensure that a job executes on worker threads after the dependency has completed execution.
        /// Making sure that two jobs reading or writing to same data do not run in parallel.
        /// </param>
        /// <returns>
        /// The handle identifying the scheduled job. Can be used as a dependency for a later job or ensure completion on the main thread.
        /// </returns>
        public static JobHandle Schedule(this Triangulator<int2> @this, JobHandle dependencies = default) =>
        new TriangulationJob<int, int2, long, TransformInt, IntUtils>(@this).Schedule(dependencies);

#if UNITY_MATHEMATICS_FIXEDPOINT
		/// <summary>
		/// Perform the job's Execute method immediately on the same thread.
		/// </summary>
		public static void Run(this Triangulator<fp2> @this) =>
		new TriangulationJob<fp, fp2, fp, TransformFp, FpUtils>(@this).Run();
		/// <summary>
		/// Schedule the job for execution on a worker thread.
		/// </summary>
		/// <param name="dependencies">
		/// Dependencies are used to ensure that a job executes on worker threads after the dependency has completed execution.
		/// Making sure that two jobs reading or writing to same data do not run in parallel.
		/// </param>
		/// <returns>
		/// The handle identifying the scheduled job. Can be used as a dependency for a later job or ensure completion on the main thread.
		/// </returns>
		public static JobHandle Schedule(this Triangulator<fp2> @this, JobHandle dependencies = default) =>
		new TriangulationJob<fp, fp2, fp, TransformFp, FpUtils>(@this).Schedule(dependencies);
#endif
    }
}

namespace andywiecko.BurstTriangulator.LowLevel.Unsafe
{
    /// <summary>
    /// Native correspondence to <see cref="BurstTriangulator.InputData{T2}"/>.
    /// </summary>
    /// <seealso cref="BurstTriangulator.InputData{T2}"/>
    public struct InputData<T2> where T2 : unmanaged
    {
        /// <summary>
        /// Positions of points used in triangulation.
        /// </summary>
        public NativeArray<T2> Positions;
        /// <summary>
        /// Optional buffer for constraint edges. This array constrains specific edges to be included in the final
        /// triangulation result. It should contain indexes corresponding to the <see cref="Positions"/> of the edges
        /// in the format [a₀, a₁, b₀, b₁, c₀, c₁, ...], where (a₀, a₁), (b₀, b₁), (c₀, c₁), etc., represent the constraint edges.
        /// </summary>
        /// <remarks>
        /// <b>Note:</b> If refinement is enabled, the provided constraints may be split during the refinement process.
        /// </remarks>
        public NativeArray<int> ConstraintEdges;
        public NativeArray<ConstraintType> ConstraintEdgeTypes;
        /// <summary>
        /// Optional buffer containing seeds for holes. These hole seeds serve as starting points for a removal process that
        /// mimics the spread of a virus. During this process, <see cref="ConstraintEdges"/> act as barriers to prevent further propagation.
        /// For more information, refer to the documentation.
        /// </summary>
        public NativeArray<T2> HoleSeeds;
    }

    /// <summary>
    /// Native correspondence to <see cref="BurstTriangulator.OutputData{T2}"/>.
    /// </summary>
    /// <seealso cref="BurstTriangulator.OutputData{T2}"/>
    public struct OutputData<T2> where T2 : unmanaged
    {
        /// <summary>
        /// Positions of triangulation points.
        /// </summary>
        /// <remarks>
        /// <b>Note:</b> This buffer may include additional points than <see cref="InputData{T2}.Positions"/> if refinement is enabled.
        /// Additionally, the positions might differ slightly (by a small ε) if a <see cref="Args.Preprocessor"/> is applied.
        /// </remarks>
        public NativeList<T2> Positions;
        /// <summary>
        /// Continuous buffer of resulting triangles. All triangles are guaranteed to be oriented clockwise.
        /// </summary>
        public NativeList<int> Triangles;
        /// <summary>
        /// Status of the triangulation. Retrieve this value to detect any errors that occurred during triangulation.
        /// </summary>
        public NativeReference<Status> Status;
        /// <summary>
        /// Continuous buffer of resulting halfedges. A value of -1 indicates that there is no corresponding opposite halfedge.
        /// For more information, refer to the documentation on halfedges.
        /// </summary>
        public NativeList<int> Halfedges;
        /// <summary>
        /// Buffer corresponding to <see cref="Halfedges"/>.
        /// </summary>
        public NativeList<HalfedgeState> ConstrainedHalfedges;
    }

    /// <summary>
    /// Native correspondence to <see cref="TriangulationSettings"/>.
    /// </summary>
    /// <seealso cref="TriangulationSettings"/>
    public readonly struct Args
    {
        public readonly Preprocessor Preprocessor;
        public readonly int SloanMaxIters;
        // NOTE: Only blittable types are supported for Burst compiled static methods.
        //       Unfortunately bool type is non-blittable and required marshaling for compilation.
        //       Learn more about blittable here: https://learn.microsoft.com/en-us/dotnet/framework/interop/blittable-and-non-blittable-types
        [MarshalAs(UnmanagedType.U1)]
        public readonly bool AutoHolesAndBoundary, RefineMesh, RestoreBoundary, ValidateInput, Verbose;
        public readonly float RefinementThresholdAngle, RefinementThresholdArea;

        /// <summary>
        /// Constructs a new <see cref="Args"/>.
        /// </summary>
        /// <remarks>
        /// Use <see cref="Default"/> and <see cref="With"/> for easy construction.
        /// </remarks>
        public Args(
            Preprocessor preprocessor,
            int sloanMaxIters,
            bool autoHolesAndBoundary, bool refineMesh, bool restoreBoundary, bool validateInput, bool verbose,
            float refinementThresholdAngle, float refinementThresholdArea
            )
        {
            AutoHolesAndBoundary = autoHolesAndBoundary;
            Preprocessor = preprocessor;
            RefineMesh = refineMesh;
            RestoreBoundary = restoreBoundary;
            SloanMaxIters = sloanMaxIters;
            ValidateInput = validateInput;
            Verbose = verbose;
            RefinementThresholdAngle = refinementThresholdAngle;
            RefinementThresholdArea = refinementThresholdArea;
        }

        /// <summary>
        /// Construct <see cref="Args"/> with default values (same as <see cref="TriangulationSettings"/> defaults).
        /// </summary>
        public static Args Default(
            Preprocessor preprocessor = Preprocessor.None,
            int sloanMaxIters = 1_000_000,
            bool autoHolesAndBoundary = false, bool refineMesh = false, bool restoreBoundary = false, bool validateInput = true, bool verbose = true,
            float refinementThresholdAngle = 0.0872664626f, float refinementThresholdArea = 1f
            ) => new(
            preprocessor,
            sloanMaxIters,
            autoHolesAndBoundary, refineMesh, restoreBoundary, validateInput, verbose,
            refinementThresholdAngle, refinementThresholdArea
            );

        public static implicit operator Args(TriangulationSettings settings) => new(
            autoHolesAndBoundary: settings.AutoHolesAndBoundary,
            preprocessor: settings.Preprocessor,
            refineMesh: settings.RefineMesh,
            restoreBoundary: settings.RestoreBoundary,
            sloanMaxIters: settings.SloanMaxIters,
            validateInput: settings.ValidateInput,
            verbose: settings.Verbose,
            refinementThresholdAngle: settings.RefinementThresholds.Angle,
            refinementThresholdArea: settings.RefinementThresholds.Area
            );

        /// <summary>
        /// Returns a new <see cref="Args"/> but with changed selected parameter(s) values.
        /// </summary>
        public Args With(
            Preprocessor? preprocessor = null,
            int? sloanMaxIters = null,
            bool? autoHolesAndBoundary = null, bool? refineMesh = null, bool? restoreBoundary = null, bool? validateInput = null, bool? verbose = null,
            float? refinementThresholdAngle = null, float? refinementThresholdArea = null
            ) => new(
            preprocessor ?? Preprocessor,
            sloanMaxIters ?? SloanMaxIters,
            autoHolesAndBoundary ?? AutoHolesAndBoundary, refineMesh ?? RefineMesh, restoreBoundary ?? RestoreBoundary, validateInput ?? ValidateInput, verbose ?? Verbose,
            refinementThresholdAngle ?? RefinementThresholdAngle, refinementThresholdArea ?? RefinementThresholdArea
            );
    }

    /// <summary>
    /// A wrapper for <see cref="UnsafeTriangulator{T2}"/> where T2 is <see cref="double2"/>.
    /// </summary>
    /// <seealso cref="UnsafeTriangulator{T2}"/>
    /// <seealso cref="Extensions"/>
    public readonly struct UnsafeTriangulator { }

    /// <summary>
    /// A readonly struct that corresponds to <see cref="Triangulator{T2}"/>.
    /// This struct can be used directly in a native context within the jobs pipeline.
    /// The API is accessible through <see cref="Extensions"/>.
    /// </summary>
    /// <remarks>
    /// <i>Unsafe</i> in this context indicates that using the method may be challenging for beginner users.
    /// The user is responsible for managing data allocation (both input and output).
    /// Some permutations of the method calls may not be supported.
    /// Refer to the documentation for more details. The term <i>unsafe</i> does <b>not</b> refer to memory safety.
    /// </remarks>
    /// <typeparam name="T2">The coordinate type. Supported types include:
    /// <see cref="float2"/>,
    /// <see cref="Vector2"/>,
    /// <see cref="double2"/>,
    /// <see cref="fp2"/>,
    /// and
    /// <see cref="int2"/>.
    /// For more information on type restrictions, refer to the documentation.
    /// </typeparam>
    /// <seealso cref="Extensions"/>
    public readonly struct UnsafeTriangulator<T2> where T2 : unmanaged { }

    public static class Extensions
    {
        /// <summary>
        /// Performs triangulation on the given <paramref name="input"/>, producing the result in <paramref name="output"/> based on the settings specified in <paramref name="args"/>.
        /// This method corresponds to the native implementation of <see cref="Triangulator.Run"/>.
        /// </summary>
        /// <remarks>
        /// <b>Note:</b>
        /// The <paramref name="input"/> and <paramref name="output"/> native containers must be allocated by the user. Some buffers are optional; refer to the documentation for more details.
        /// </remarks>
        /// <param name="allocator">The allocator to use. If called from a job, consider using <see cref="Allocator.Temp"/>.</param>
        public static void Triangulate(this UnsafeTriangulator @this, InputData<double2> input, OutputData<double2> output, Args args, Allocator allocator) => new UnsafeTriangulator<double, double2, double, TransformDouble, DoubleUtils>().Triangulate(input, output, args, allocator);
        /// <summary>
        /// Plants hole seeds defined in <paramref name="input"/> (or restores boundaries or auto-holes if specified in <paramref name="args"/>)
        /// within the triangulation data in <paramref name="output"/>, using the settings specified in <paramref name="args"/>.
        /// </summary>
        /// <remarks>
        /// <b>Note:</b>
        /// This method requires that <paramref name="output"/> contains valid triangulation data.
        /// The <paramref name="input"/> and <paramref name="output"/> native containers must be allocated by the user. Some buffers are optional; refer to the documentation for more details.
        /// </remarks>
        /// <param name="allocator">The allocator to use. If called from a job, consider using <see cref="Allocator.Temp"/>.</param>
        public static void PlantHoleSeeds(this UnsafeTriangulator @this, InputData<double2> input, OutputData<double2> output, Args args, Allocator allocator) => new UnsafeTriangulator<double, double2, double, TransformDouble, DoubleUtils>().PlantHoleSeeds(input, output, args, allocator);
        /// <summary>
        /// Refines the mesh for a valid triangulation in <paramref name="output"/>.
        /// Refinement parameters can be provided with the selected precision type T in generics, which is especially useful for fixed-point arithmetic.
        /// Refinement parameters in <see cref="Args"/> are restricted to <see cref="float"/> precision.
        /// </summary>
        /// <remarks>
        /// <b>Note:</b>
        /// This method requires that <paramref name="output"/> contains valid triangulation data.
        /// The <paramref name="input"/> and <paramref name="output"/> native containers must be allocated by the user. Some buffers are optional; refer to the documentation for more details.
        /// </remarks>
        /// <param name="allocator">The allocator to use. If called from a job, consider using <see cref="Allocator.Temp"/>.</param>
        /// <param name="angleThreshold">Expressed in <em>radians</em>. Default: 5° = 0.0872664626 rad.</param>
        /// <param name="constrainBoundary">Used to constrain boundary halfedges. Since the refinement algorithm (whether for constrained triangulation or not) requires constrained halfedges at the boundary, not setting this option may cause unexpected behavior, especially when the restoreBoundary option is disabled.</param>
        public static void RefineMesh(this UnsafeTriangulator @this, OutputData<double2> output, Allocator allocator, double areaThreshold = 1, double angleThreshold = 0.0872664626, bool constrainBoundary = false) => new UnsafeTriangulator<double, double2, double, TransformDouble, DoubleUtils>().RefineMesh(output, allocator, 2 * areaThreshold, angleThreshold, constrainBoundary);

        /// <summary>
        /// Performs triangulation on the given <paramref name="input"/>, producing the result in <paramref name="output"/> based on the settings specified in <paramref name="args"/>.
        /// This method corresponds to the native implementation of <see cref="Triangulator.Run"/>.
        /// </summary>
        /// <remarks>
        /// <b>Note:</b>
        /// The <paramref name="input"/> and <paramref name="output"/> native containers must be allocated by the user. Some buffers are optional; refer to the documentation for more details.
        /// </remarks>
        /// <param name="allocator">The allocator to use. If called from a job, consider using <see cref="Allocator.Temp"/>.</param>
        public static void Triangulate(this UnsafeTriangulator<float2> @this, InputData<float2> input, OutputData<float2> output, Args args, Allocator allocator) => new UnsafeTriangulator<float, float2, float, TransformFloat, FloatUtils>().Triangulate(input, output, args, allocator);
        /// <summary>
        /// Plants hole seeds defined in <paramref name="input"/> (or restores boundaries or auto-holes if specified in <paramref name="args"/>)
        /// within the triangulation data in <paramref name="output"/>, using the settings specified in <paramref name="args"/>.
        /// </summary>
        /// <remarks>
        /// <b>Note:</b>
        /// This method requires that <paramref name="output"/> contains valid triangulation data.
        /// The <paramref name="input"/> and <paramref name="output"/> native containers must be allocated by the user. Some buffers are optional; refer to the documentation for more details.
        /// </remarks>
        /// <param name="allocator">The allocator to use. If called from a job, consider using <see cref="Allocator.Temp"/>.</param>
        public static void PlantHoleSeeds(this UnsafeTriangulator<float2> @this, InputData<float2> input, OutputData<float2> output, Args args, Allocator allocator) => new UnsafeTriangulator<float, float2, float, TransformFloat, FloatUtils>().PlantHoleSeeds(input, output, args, allocator);
        /// <summary>
        /// Refines the mesh for a valid triangulation in <paramref name="output"/>.
        /// Refinement parameters can be provided with the selected precision type T in generics, which is especially useful for fixed-point arithmetic.
        /// Refinement parameters in <see cref="Args"/> are restricted to <see cref="float"/> precision.
        /// </summary>
        /// <remarks>
        /// <b>Note:</b>
        /// This method requires that <paramref name="output"/> contains valid triangulation data.
        /// The <paramref name="input"/> and <paramref name="output"/> native containers must be allocated by the user. Some buffers are optional; refer to the documentation for more details.
        /// </remarks>
        /// <param name="allocator">The allocator to use. If called from a job, consider using <see cref="Allocator.Temp"/>.</param>
        /// <param name="angleThreshold">Expressed in <em>radians</em>. Default: 5° = 0.0872664626 rad.</param>
        /// <param name="constrainBoundary">Used to constrain boundary halfedges. Since the refinement algorithm (whether for constrained triangulation or not) requires constrained halfedges at the boundary, not setting this option may cause unexpected behavior, especially when the restoreBoundary option is disabled.</param>
        public static void RefineMesh(this UnsafeTriangulator<float2> @this, OutputData<float2> output, Allocator allocator, float areaThreshold = 1, float angleThreshold = 0.0872664626f, bool constrainBoundary = false) => new UnsafeTriangulator<float, float2, float, TransformFloat, FloatUtils>().RefineMesh(output, allocator, 2 * areaThreshold, angleThreshold, constrainBoundary);

        /// <summary>
        /// Performs triangulation on the given <paramref name="input"/>, producing the result in <paramref name="output"/> based on the settings specified in <paramref name="args"/>.
        /// This method corresponds to the native implementation of <see cref="Triangulator.Run"/>.
        /// </summary>
        /// <remarks>
        /// <b>Note:</b>
        /// The <paramref name="input"/> and <paramref name="output"/> native containers must be allocated by the user. Some buffers are optional; refer to the documentation for more details.
        /// </remarks>
        /// <param name="allocator">The allocator to use. If called from a job, consider using <see cref="Allocator.Temp"/>.</param>
        public static void Triangulate(this UnsafeTriangulator<Vector2> @this, InputData<Vector2> input, OutputData<Vector2> output, Args args, Allocator allocator) => new UnsafeTriangulator<float, float2, float, TransformFloat, FloatUtils>().Triangulate(UnsafeUtility.As<InputData<Vector2>, InputData<float2>>(ref input), UnsafeUtility.As<OutputData<Vector2>, OutputData<float2>>(ref output), args, allocator);

        /// <summary>
        /// Plants hole seeds defined in <paramref name="input"/> (or restores boundaries or auto-holes if specified in <paramref name="args"/>)
        /// within the triangulation data in <paramref name="output"/>, using the settings specified in <paramref name="args"/>.
        /// </summary>
        /// <remarks>
        /// <b>Note:</b>
        /// This method requires that <paramref name="output"/> contains valid triangulation data.
        /// The <paramref name="input"/> and <paramref name="output"/> native containers must be allocated by the user. Some buffers are optional; refer to the documentation for more details.
        /// </remarks>
        /// <param name="allocator">The allocator to use. If called from a job, consider using <see cref="Allocator.Temp"/>.</param>
        public static void PlantHoleSeeds(this UnsafeTriangulator<Vector2> @this, InputData<Vector2> input, OutputData<Vector2> output, Args args, Allocator allocator) => new UnsafeTriangulator<float, float2, float, TransformFloat, FloatUtils>().PlantHoleSeeds(UnsafeUtility.As<InputData<Vector2>, InputData<float2>>(ref input), UnsafeUtility.As<OutputData<Vector2>, OutputData<float2>>(ref output), args, allocator);

        /// <summary>
        /// Refines the mesh for a valid triangulation in <paramref name="output"/>.
        /// Refinement parameters can be provided with the selected precision type T in generics, which is especially useful for fixed-point arithmetic.
        /// Refinement parameters in <see cref="Args"/> are restricted to <see cref="float"/> precision.
        /// </summary>
        /// <remarks>
        /// <b>Note:</b>
        /// This method requires that <paramref name="output"/> contains valid triangulation data.
        /// The <paramref name="input"/> and <paramref name="output"/> native containers must be allocated by the user. Some buffers are optional; refer to the documentation for more details.
        /// </remarks>
        /// <param name="allocator">The allocator to use. If called from a job, consider using <see cref="Allocator.Temp"/>.</param>
        /// <param name="angleThreshold">Expressed in <em>radians</em>. Default: 5° = 0.0872664626 rad.</param>
        /// <param name="constrainBoundary">Used to constrain boundary halfedges. Since the refinement algorithm (whether for constrained triangulation or not) requires constrained halfedges at the boundary, not setting this option may cause unexpected behavior, especially when the restoreBoundary option is disabled.</param>
        public static void RefineMesh(this UnsafeTriangulator<Vector2> @this, OutputData<Vector2> output, Allocator allocator, float areaThreshold = 1, float angleThreshold = 0.0872664626f, bool constrainBoundary = false) => new UnsafeTriangulator<float, float2, float, TransformFloat, FloatUtils>().RefineMesh(UnsafeUtility.As<OutputData<Vector2>, OutputData<float2>>(ref output), allocator, 2 * areaThreshold, angleThreshold, constrainBoundary);

        /// <summary>
        /// Performs triangulation on the given <paramref name="input"/>, producing the result in <paramref name="output"/> based on the settings specified in <paramref name="args"/>.
        /// This method corresponds to the native implementation of <see cref="Triangulator.Run"/>.
        /// </summary>
        /// <remarks>
        /// <b>Note:</b>
        /// The <paramref name="input"/> and <paramref name="output"/> native containers must be allocated by the user. Some buffers are optional; refer to the documentation for more details.
        /// </remarks>
        /// <param name="allocator">The allocator to use. If called from a job, consider using <see cref="Allocator.Temp"/>.</param>
        public static void Triangulate(this UnsafeTriangulator<double2> @this, InputData<double2> input, OutputData<double2> output, Args args, Allocator allocator) => new UnsafeTriangulator<double, double2, double, TransformDouble, DoubleUtils>().Triangulate(input, output, args, allocator);
        /// <summary>
        /// Plants hole seeds defined in <paramref name="input"/> (or restores boundaries or auto-holes if specified in <paramref name="args"/>)
        /// within the triangulation data in <paramref name="output"/>, using the settings specified in <paramref name="args"/>.
        /// </summary>
        /// <remarks>
        /// <b>Note:</b>
        /// This method requires that <paramref name="output"/> contains valid triangulation data.
        /// The <paramref name="input"/> and <paramref name="output"/> native containers must be allocated by the user. Some buffers are optional; refer to the documentation for more details.
        /// </remarks>
        /// <param name="allocator">The allocator to use. If called from a job, consider using <see cref="Allocator.Temp"/>.</param>
        public static void PlantHoleSeeds(this UnsafeTriangulator<double2> @this, InputData<double2> input, OutputData<double2> output, Args args, Allocator allocator) => new UnsafeTriangulator<double, double2, double, TransformDouble, DoubleUtils>().PlantHoleSeeds(input, output, args, allocator);

        /// <summary>
        /// Refines the mesh for a valid triangulation in <paramref name="output"/>.
        /// Refinement parameters can be provided with the selected precision type T in generics, which is especially useful for fixed-point arithmetic.
        /// Refinement parameters in <see cref="Args"/> are restricted to <see cref="float"/> precision.
        /// </summary>
        /// <remarks>
        /// <b>Note:</b>
        /// This method requires that <paramref name="output"/> contains valid triangulation data.
        /// The <paramref name="input"/> and <paramref name="output"/> native containers must be allocated by the user. Some buffers are optional; refer to the documentation for more details.
        /// </remarks>
        /// <param name="allocator">The allocator to use. If called from a job, consider using <see cref="Allocator.Temp"/>.</param>
        /// <param name="angleThreshold">Expressed in <em>radians</em>. Default: 5° = 0.0872664626 rad.</param>
        /// <param name="constrainBoundary">Used to constrain boundary halfedges. Since the refinement algorithm (whether for constrained triangulation or not) requires constrained halfedges at the boundary, not setting this option may cause unexpected behavior, especially when the restoreBoundary option is disabled.</param>
        public static void RefineMesh(this UnsafeTriangulator<double2> @this, OutputData<double2> output, Allocator allocator, double areaThreshold = 1, double angleThreshold = 0.0872664626, bool constrainBoundary = false) => new UnsafeTriangulator<double, double2, double, TransformDouble, DoubleUtils>().RefineMesh(output, allocator, 2 * areaThreshold, angleThreshold, constrainBoundary);

        /// <summary>
        /// Performs triangulation on the given <paramref name="input"/>, producing the result in <paramref name="output"/> based on the settings specified in <paramref name="args"/>.
        /// This method corresponds to the native implementation of <see cref="Triangulator.Run"/>.
        /// </summary>
        /// <remarks>
        /// <b>Note:</b>
        /// The <paramref name="input"/> and <paramref name="output"/> native containers must be allocated by the user. Some buffers are optional; refer to the documentation for more details.
        /// </remarks>
        /// <param name="allocator">The allocator to use. If called from a job, consider using <see cref="Allocator.Temp"/>.</param>
        public static void Triangulate(this UnsafeTriangulator<int2> @this, InputData<int2> input, OutputData<int2> output, Args args, Allocator allocator) => new UnsafeTriangulator<int, int2, long, TransformInt, IntUtils>().Triangulate(input, output, args, allocator);
        /// <summary>
        /// Plants hole seeds defined in <paramref name="input"/> (or restores boundaries or auto-holes if specified in <paramref name="args"/>)
        /// within the triangulation data in <paramref name="output"/>, using the settings specified in <paramref name="args"/>.
        /// </summary>
        /// <remarks>
        /// <b>Note:</b>
        /// This method requires that <paramref name="output"/> contains valid triangulation data.
        /// The <paramref name="input"/> and <paramref name="output"/> native containers must be allocated by the user. Some buffers are optional; refer to the documentation for more details.
        /// </remarks>
        /// <param name="allocator">The allocator to use. If called from a job, consider using <see cref="Allocator.Temp"/>.</param>
        public static void PlantHoleSeeds(this UnsafeTriangulator<int2> @this, InputData<int2> input, OutputData<int2> output, Args args, Allocator allocator) => new UnsafeTriangulator<int, int2, long, TransformInt, IntUtils>().PlantHoleSeeds(input, output, args, allocator);

#if UNITY_MATHEMATICS_FIXEDPOINT
		/// <summary>
		/// Performs triangulation on the given <paramref name="input"/>, producing the result in <paramref name="output"/> based on the settings specified in <paramref name="args"/>.
		/// This method corresponds to the native implementation of <see cref="Triangulator.Run"/>.
		/// </summary>
		/// <remarks>
		/// <b>Note:</b>
		/// The <paramref name="input"/> and <paramref name="output"/> native containers must be allocated by the user. Some buffers are optional; refer to the documentation for more details.
		/// </remarks>
		/// <param name="allocator">The allocator to use. If called from a job, consider using <see cref="Allocator.Temp"/>.</param>
		public static void Triangulate(this UnsafeTriangulator<fp2> @this, InputData<fp2> input, OutputData<fp2> output, Args args, Allocator allocator) => new UnsafeTriangulator<fp, fp2, fp, TransformFp, FpUtils>().Triangulate(input, output, args, allocator);
		/// <summary>
		/// Plants hole seeds defined in <paramref name="input"/> (or restores boundaries or auto-holes if specified in <paramref name="args"/>)
		/// within the triangulation data in <paramref name="output"/>, using the settings specified in <paramref name="args"/>.
		/// </summary>
		/// <remarks>
		/// <b>Note:</b>
		/// This method requires that <paramref name="output"/> contains valid triangulation data.
		/// The <paramref name="input"/> and <paramref name="output"/> native containers must be allocated by the user. Some buffers are optional; refer to the documentation for more details.
		/// </remarks>
		/// <param name="allocator">The allocator to use. If called from a job, consider using <see cref="Allocator.Temp"/>.</param>
		public static void PlantHoleSeeds(this UnsafeTriangulator<fp2> @this, InputData<fp2> input, OutputData<fp2> output, Args args, Allocator allocator) => new UnsafeTriangulator<fp, fp2, fp, TransformFp, FpUtils>().PlantHoleSeeds(input, output, args, allocator);
		/// <summary>
		/// Refines the mesh for a valid triangulation in <paramref name="output"/>.
		/// Refinement parameters can be provided with the selected precision type T in generics, which is especially useful for fixed-point arithmetic.
		/// Refinement parameters in <see cref="Args"/> are restricted to <see cref="float"/> precision.
		/// </summary>
		/// <remarks>
		/// <b>Note:</b>
		/// This method requires that <paramref name="output"/> contains valid triangulation data.
		/// The <paramref name="input"/> and <paramref name="output"/> native containers must be allocated by the user. Some buffers are optional; refer to the documentation for more details.
		/// </remarks>
		/// <param name="allocator">The allocator to use. If called from a job, consider using <see cref="Allocator.Temp"/>.</param>
		/// <param name="angleThreshold">Expressed in <em>radians</em>. Default: 5° = 0.0872664626 rad.</param>
		/// <param name="constrainBoundary">Used to constrain boundary halfedges. Since the refinement algorithm (whether for constrained triangulation or not) requires constrained halfedges at the boundary, not setting this option may cause unexpected behavior, especially when the restoreBoundary option is disabled.</param>
		public static void RefineMesh(this UnsafeTriangulator<fp2> @this, OutputData<fp2> output, Allocator allocator, fp? areaThreshold = null, fp? angleThreshold = null, fp? concentricShells = null, bool constrainBoundary = false) => new UnsafeTriangulator<fp, fp2, fp, TransformFp, FpUtils>().RefineMesh(output, allocator, 2 * (areaThreshold ?? 1), angleThreshold ?? fp.FromRaw(374806602) /*Raw value for (fp)0.0872664626*/, concentricShells ?? fp.FromRaw(4294967) /*Raw value for (fp)1 / 1000*/, constrainBoundary);
#endif
    }

    [BurstCompile]
    internal struct TriangulationJob<T, T2, TBig, TTransform, TUtils> : IJob
        where T : unmanaged, IComparable<T>
        where T2 : unmanaged
        where TBig : unmanaged, IComparable<TBig>
        where TTransform : unmanaged, ITransform<TTransform, T, T2>
        where TUtils : unmanaged, IUtils<T, T2, TBig>
    {
        private NativeArray<T2> inputPositions;
        [NativeDisableContainerSafetyRestriction]
        private NativeArray<int> constraints;
        [NativeDisableContainerSafetyRestriction]
        private NativeArray<ConstraintType> constraintTypes;
        [NativeDisableContainerSafetyRestriction]
        private NativeArray<T2> holeSeeds;

        private NativeList<T2> outputPositions;
        private NativeList<int> triangles;
        private NativeList<int> halfedges;
        private NativeList<HalfedgeState> constrainedHalfedges;
        private NativeReference<Status> status;

        private readonly Args args;

        public TriangulationJob(InputData<T2> input, OutputData<T2> output, Args args)
        {
            inputPositions = input.Positions;
            constraints = input.ConstraintEdges;
            constraintTypes = input.ConstraintEdgeTypes;
            holeSeeds = input.HoleSeeds;

            outputPositions = output.Positions;
            triangles = output.Triangles;
            halfedges = output.Halfedges;
            constrainedHalfedges = output.ConstrainedHalfedges;
            status = output.Status;

            this.args = args;
        }

        public TriangulationJob(Triangulator<T2> @this)
        {
            inputPositions = @this.Input.Positions;
            constraints = @this.Input.ConstraintEdges;
            constraintTypes = @this.Input.ConstraintEdgeTypes;
            holeSeeds = @this.Input.HoleSeeds;

            outputPositions = @this.Output.Positions;
            triangles = @this.Output.Triangles;
            halfedges = @this.Output.Halfedges;
            constrainedHalfedges = @this.Output.ConstrainedHalfedges;
            status = @this.Output.Status;

            args = @this.Settings;
        }

        public void Execute()
        {
            new UnsafeTriangulator<T, T2, TBig, TTransform, TUtils>().Triangulate(
                input: new()
                {
                    Positions = inputPositions,
                    ConstraintEdges = constraints,
                    ConstraintEdgeTypes = constraintTypes,
                    HoleSeeds = holeSeeds,
                },
                output: new()
                {
                    Positions = outputPositions,
                    Triangles = triangles,
                    Halfedges = halfedges,
                    ConstrainedHalfedges = constrainedHalfedges,
                    Status = status,
                }, args, Allocator.Temp);
        }
    }

    internal readonly struct UnsafeTriangulator<T, T2, TBig, TTransform, TUtils>
        where T : unmanaged, IComparable<T>
        where T2 : unmanaged
        where TBig : unmanaged, IComparable<TBig>
        where TTransform : unmanaged, ITransform<TTransform, T, T2>
        where TUtils : unmanaged, IUtils<T, T2, TBig>
    {
        // NOTE: Caching ProfileMarker can boost performance for triangulations with small input (~10² triangles).
        private readonly struct Markers
        {
            public static readonly ProfilerMarker PreProcessInputStep = new(nameof(UnsafeTriangulator<T, T2, TBig, TTransform, TUtils>.PreProcessInputStep));
            public static readonly ProfilerMarker PostProcessInputStep = new(nameof(UnsafeTriangulator<T, T2, TBig, TTransform, TUtils>.PostProcessInputStep));
            public static readonly ProfilerMarker ValidateInputStep = new(nameof(UnsafeTriangulator<T, T2, TBig, TTransform, TUtils>.ValidateInputStep));
            public static readonly ProfilerMarker DelaunayTriangulationStep = new(nameof(UnsafeTriangulator<T, T2, TBig, TTransform, TUtils>.DelaunayTriangulationStep));
            public static readonly ProfilerMarker ConstrainEdgesStep = new(nameof(UnsafeTriangulator<T, T2, TBig, TTransform, TUtils>.ConstrainEdgesStep));
            public static readonly ProfilerMarker PlantingSeedStep = new(nameof(UnsafeTriangulator<T, T2, TBig, TTransform, TUtils>.PlantingSeedStep));
            public static readonly ProfilerMarker RefineMeshStep = new(nameof(UnsafeTriangulator<T, T2, TBig, TTransform, TUtils>.RefineMeshStep));
        }

        private static readonly TUtils utils = default;

        public void Triangulate(InputData<T2> input, OutputData<T2> output, Args args, Allocator allocator)
        {
            var tmpStatus = default(NativeReference<Status>);
            var tmpPositions = default(NativeList<T2>);
            var tmpHalfedges = default(NativeList<int>);
            var tmpConstrainedHalfedges = default(NativeList<HalfedgeState>);
            if (!output.Status.IsCreated) output.Status = tmpStatus = new(allocator);
            if (!output.Positions.IsCreated) output.Positions = tmpPositions = new(16 * 1024, allocator);
            if (!output.Halfedges.IsCreated) output.Halfedges = tmpHalfedges = new(6 * 16 * 1024, allocator);
            if (!output.ConstrainedHalfedges.IsCreated) output.ConstrainedHalfedges = tmpConstrainedHalfedges = new(6 * 16 * 1024, allocator);

            output.Status.Value = Status.Ok;
            output.Triangles.Clear();
            output.Positions.Clear();
            output.Halfedges.Clear();
            output.ConstrainedHalfedges.Clear();

            // After this step, the positions have been moved to output.Positions (possibly modified by a preprocessor)
            PreProcessInputStep(input, output, args, out var localHoles, out var lt, allocator);
            new ValidateInputStep(input, output, args).Execute();
            new DelaunayTriangulationStep(output, args).Execute(allocator);
            new ConstrainEdgesStep(input, output, args).Execute(allocator);
            new PlantingSeedStep(output, args, localHoles).Execute(allocator, input.ConstraintEdges.IsCreated);
            new RefineMeshStep(output, args, lt).Execute(allocator, refineMesh: args.RefineMesh, constrainBoundary: !input.ConstraintEdges.IsCreated || !args.RestoreBoundary);
            PostProcessInputStep(output, args, lt);

            var status = output.Status.Value;
            if (localHoles.IsCreated) localHoles.Dispose();
            if (tmpStatus.IsCreated) tmpStatus.Dispose();
            if (tmpPositions.IsCreated) tmpPositions.Dispose();
            if (tmpHalfedges.IsCreated) tmpHalfedges.Dispose();
            if (tmpConstrainedHalfedges.IsCreated) tmpConstrainedHalfedges.Dispose();

            if (args.Verbose && status.IsError) Debug.LogError(status.ToFixedString());
        }

        public void PlantHoleSeeds(InputData<T2> input, OutputData<T2> output, Args args, Allocator allocator)
        {
            new PlantingSeedStep(input, output, args).Execute(allocator, true);
        }

        public void RefineMesh(OutputData<T2> output, Allocator allocator, T area2Threshold, T angleThreshold, bool constrainBoundary = false)
        {
            new RefineMeshStep(output, area2Threshold, angleThreshold).Execute(allocator, refineMesh: true, constrainBoundary);
        }

        private void PreProcessInputStep(InputData<T2> input, OutputData<T2> output, Args args, out NativeArray<T2> localHoles, out TTransform lt, Allocator allocator)
        {
            using var _ = Markers.PreProcessInputStep.Auto();

            var localPositions = output.Positions;
            localPositions.ResizeUninitialized(input.Positions.Length);
            if (args.Preprocessor == Preprocessor.PCA || args.Preprocessor == Preprocessor.COM)
            {
                lt = args.Preprocessor == Preprocessor.PCA ? default(TTransform).CalculatePCATransformation(input.Positions) : default(TTransform).CalculateLocalTransformation(input.Positions);
                for (int i = 0; i < input.Positions.Length; i++)
                {
                    localPositions[i] = lt.Transform(input.Positions[i]);
                }

                localHoles = input.HoleSeeds.IsCreated ? new(input.HoleSeeds.Length, allocator) : default;
                for (int i = 0; i < input.HoleSeeds.Length; i++)
                {
                    localHoles[i] = lt.Transform(input.HoleSeeds[i]);
                }
            }
            else if (args.Preprocessor == Preprocessor.None)
            {
                localPositions.CopyFrom(input.Positions);
                localHoles = input.HoleSeeds.IsCreated ? new(input.HoleSeeds, allocator) : default;
                lt = default(TTransform).Identity;
            }
            else
            {
                throw new ArgumentException();
            }
        }

        private void PostProcessInputStep(OutputData<T2> output, Args args, TTransform lt)
        {
            if (args.Preprocessor == Preprocessor.None)
            {
                return;
            }

            using var _ = Markers.PostProcessInputStep.Auto();
            var inverse = lt.Inverse();
            for (int i = 0; i < output.Positions.Length; i++)
            {
                output.Positions[i] = inverse.Transform(output.Positions[i]);
            }
        }

        private struct ValidateInputStep
        {
            private NativeArray<T2>.ReadOnly positions;
            private NativeReference<Status> status;
            private readonly Args args;
            private NativeArray<int>.ReadOnly constraints;
            private NativeArray<ConstraintType>.ReadOnly constraintTypes;
            private NativeArray<T2>.ReadOnly holes;

            public ValidateInputStep(InputData<T2> input, OutputData<T2> output, Args args)
            {
                positions = output.Positions.AsReadOnly();
                status = output.Status;
                this.args = args;
                constraints = input.ConstraintEdges.AsReadOnly();
                constraintTypes = input.ConstraintEdgeTypes.AsReadOnly();
                holes = input.HoleSeeds.AsReadOnly();
            }

            public void Execute()
            {
                if (!args.ValidateInput)
                {
                    return;
                }

                using var _ = Markers.ValidateInputStep.Auto();

                ValidateArgs();
                ValidatePositions();
                ValidateConstraints();
                ValidateHoles();
            }

            private void ValidateArgs()
            {
                if (args.AutoHolesAndBoundary && !constraints.IsCreated)
                {
                    status.Value = Status.ConstraintEdgesMissingForAutoHolesAndBoundary;
                }

                if (args.RestoreBoundary && !constraints.IsCreated)
                {
                    status.Value = Status.ConstraintEdgesMissingForRestoreBoundary;
                }

                if (args.RefineMesh && !utils.SupportsRefinement())
                {
                    status.Value = Status.RefinementNotSupportedForCoordinateType;
                }

                if (constraints.IsCreated && args.SloanMaxIters < 1)
                {
                    status.Value = Status.SloanMaxItersMustBePositive(args.SloanMaxIters);
                }

                if (args.RefineMesh && args.RefinementThresholdArea < 0)
                {
                    status.Value = Status.RefinementThresholdAreaMustBePositive;
                }

                if (args.RefineMesh && args.RefinementThresholdAngle < 0 || args.RefinementThresholdAngle > math.PI / 4)
                {
                    status.Value = Status.RefinementThresholdAngleOutOfRange;
                }
            }

            private void ValidatePositions()
            {
                if (positions.Length < 3)
                {
                    status.Value = Status.PositionsLengthLessThan3(positions.Length);
                    return;
                }

                for (int i = 0; i < positions.Length; i++)
                {
                    if (math.any(!utils.isfinite(positions[i])))
                    {
                        status.Value = Status.PositionsMustBeFinite(i);
                        return;
                    }

                    var pi = positions[i];
                    for (int j = i + 1; j < positions.Length; j++)
                    {
                        var pj = positions[j];
                        if (math.all(utils.eq(pi, pj)))
                        {
                            status.Value = Status.DuplicatePosition(i);
                            return;
                        }
                    }
                }
            }

            private void ValidateConstraints()
            {
                if (!constraints.IsCreated)
                {
                    return;
                }

                if (constraints.Length % 2 != 0)
                {
                    status.Value = Status.ConstraintsLengthNotDivisibleBy2(constraints.Length);
                    return;
                }

                if (constraintTypes.IsCreated && constraintTypes.Length * 2 != constraints.Length)
                {
                    status.Value = Status.ConstraintArrayLengthMismatch(constraints.Length, constraintTypes.Length);
                    return;
                }

                // Edge validation
                for (int i = 0; i < constraints.Length / 2; i++)
                {
                    var (a0Id, a1Id) = (constraints[2 * i], constraints[2 * i + 1]);
                    var count = positions.Length;
                    if (a0Id >= count || a0Id < 0 || a1Id >= count || a1Id < 0)
                    {
                        status.Value = Status.ConstraintOutOfBounds(i, new int2(a0Id, a1Id), count);
                        return;
                    }

                    if (a0Id == a1Id)
                    {
                        status.Value = Status.ConstraintSelfLoop(i, new int2(a0Id, a1Id));
                        return;
                    }
                }

                // Edge-edge validation
                for (int i = 0; i < constraints.Length / 2; i++)
                {
                    var (a0Id, a1Id) = (constraints[2 * i], constraints[2 * i + 1]);
                    var (a0, a1) = (positions[a0Id], positions[a1Id]);

                    for (int j = i + 1; j < constraints.Length / 2; j++)
                    {
                        var (b0Id, b1Id) = (constraints[2 * j], constraints[2 * j + 1]);

                        if (a0Id == b0Id && a1Id == b1Id || a0Id == b1Id && a1Id == b0Id)
                        {
                            status.Value = Status.DuplicateConstraint(i, j);
                            return;
                        }

                        // One common point, cases should be filtered out at edge-point validation
                        if (a0Id == b0Id || a0Id == b1Id || a1Id == b0Id || a1Id == b1Id)
                        {
                            continue;
                        }

                        var (b0, b1) = (positions[b0Id], positions[b1Id]);
                        // Check if the two constraints intersect, but ignore if they only overlap at endpoints
                        if (EdgeEdgeIntersection(a0, a1, b0, b1) && !(PointLineSegmentIntersection(a0, b0, b1) || PointLineSegmentIntersection(a1, b0, b1) || PointLineSegmentIntersection(b0, a0, a1) || PointLineSegmentIntersection(b1, a0, a1)))
                        {
                            status.Value = Status.ConstraintIntersection(i, j);
                            return;
                        }
                    }
                }
            }

            private void ValidateHoles()
            {
                if (!holes.IsCreated)
                {
                    return;
                }

                if (!constraints.IsCreated)
                {
                    status.Value = Status.RedudantHolesArray;
                }

                for (int i = 0; i < holes.Length; i++)
                {
                    if (math.any(!utils.isfinite(holes[i])))
                    {
                        status.Value = Status.HoleMustBeFinite(i);
                        return;
                    }
                }
            }
        }

        /// <summary>
        /// This step is based on the following projects:
        /// <list type="bullet">
        /// <item><see href="https://github.com/mapbox/delaunator">delaunator</see></item>
        /// <item><see href="https://github.com/nol1fe/delaunator-sharp/">delaunator-sharp</see></item>
        /// </list>
        /// </summary>
        private struct DelaunayTriangulationStep
        {
            private struct DistComparer : IComparer<int>
            {
                private NativeArray<TBig> dist;
                public DistComparer(NativeArray<TBig> dist) => this.dist = dist;
                public int Compare(int x, int y) => dist[x].CompareTo(dist[y]);
            }

            private NativeReference<Status> status;
            private NativeArray<T2>.ReadOnly positions;
            private NativeList<int> triangles;
            private NativeList<int> halfedges;
            private NativeList<HalfedgeState> constrainedHalfedges;
            private NativeArray<int> hullNext, hullPrev, hullTri, hullHash;
            private NativeArray<int> EDGE_STACK;

            private readonly int hashSize;
            private readonly bool verbose;
            private int hullStart;
            private int trianglesLen;

            public DelaunayTriangulationStep(OutputData<T2> output, Args args)
            {
                status = output.Status;
                // Note: At this point these are the input positions (possibly transformed by a preprocessor)
                positions = output.Positions.AsReadOnly();
                triangles = output.Triangles;
                halfedges = output.Halfedges;
                constrainedHalfedges = output.ConstrainedHalfedges;
                hullStart = int.MaxValue;
                verbose = args.Verbose;
                hashSize = (int)math.ceil(math.sqrt(positions.Length));
                trianglesLen = default;

                hullNext = default;
                hullPrev = default;
                hullTri = default;
                hullHash = default;
                EDGE_STACK = default;
            }

            public void Execute(Allocator allocator)
            {
                if (status.Value.IsError)
                {
                    return;
                }

                using var _ = Markers.DelaunayTriangulationStep.Auto();

                var n = positions.Length;
                var maxTriangles = math.max(2 * n - 5, 0);
                triangles.Length = 3 * maxTriangles;
                halfedges.Length = 3 * maxTriangles;

                var ids = new NativeArray<int>(n, allocator);

                var min = utils.MaxValue2();
                var max = utils.MinValue2();
                for (int i = 0; i < positions.Length; i++)
                {
                    var p = positions[i];
                    min = utils.min(min, p);
                    max = utils.max(max, p);
                    ids[i] = i;
                }

                var center = utils.avg(min, max);

                int i0 = int.MaxValue, i1 = int.MaxValue, i2 = int.MaxValue;
                var minDistSq = utils.MaxValue();
                for (int i = 0; i < positions.Length; i++)
                {
                    var distSq = utils.distancesq(center, positions[i]);
                    if (utils.less(distSq, minDistSq))
                    {
                        i0 = i;
                        minDistSq = distSq;
                    }
                }

                // Centermost vertex
                var p0 = positions[i0];

                minDistSq = utils.MaxValue();
                for (int i = 0; i < positions.Length; i++)
                {
                    if (i == i0) continue;
                    var distSq = utils.distancesq(p0, positions[i]);
                    if (utils.less(distSq, minDistSq))
                    {
                        i1 = i;
                        minDistSq = distSq;
                    }
                }

                // Second closest to the center
                var p1 = positions[i1];

                var minRadius = utils.MaxValue();
                for (int i = 0; i < positions.Length; i++)
                {
                    if (i == i0 || i == i1) continue;
                    var p = positions[i];
                    var r = CircumRadiusSq(p0, p1, p);
                    if (utils.less(r, minRadius))
                    {
                        i2 = i;
                        minRadius = r;
                    }
                }

                // NOTE: Since `int` does not support NaN or infinity, a circumcenter check is required for int2 validation.
                // The CircumRadiusSq calculation may have overflowed and returned garbage values.
                if (i2 == int.MaxValue || math.any(utils.eq(utils.CircumCenter(p0, p1, positions[i2]), utils.MaxValue2())))
                {
                    status.Value = Status.DegenerateInput;
                    ids.Dispose();
                    return;
                }

                using var _hullPrev = hullPrev = new(n, allocator);
                using var _hullNext = hullNext = new(n, allocator);
                using var _hullTri = hullTri = new(n, allocator);
                using var _hullHash = hullHash = new(hashSize, allocator);
                using var _EDGE_STACK = EDGE_STACK = new(math.min(3 * maxTriangles, 512), allocator);
                var dists = new NativeArray<TBig>(n, allocator);

                // Vertex closest to p1 and p2, as measured by the circumscribed circle radius of p1, p2, p3
                // Thus (p1,p2,p3) form a triangle close to the center of the point set, and it's guaranteed that there
                // are no other vertices inside this triangle.
                var p2 = positions[i2];

                // Swap the order of the vertices if the triangle is not oriented in the right direction
                if (utils.less(Orient2dFast(p0, p1, p2), utils.ZeroTBig()))
                {
                    (i1, i2) = (i2, i1);
                    (p1, p2) = (p2, p1);
                }

                // Sort all other vertices by their distance to the circumcenter of the initial triangle
                var c = utils.CircumCenter(p0, p1, p2);

                for (int i = 0; i < positions.Length; i++)
                {
                    dists[i] = utils.distancesq(c, positions[i]);
                }

                ids.Sort(new DistComparer(dists));

                hullStart = i0;

                hullNext[i0] = hullPrev[i2] = i1;
                hullNext[i1] = hullPrev[i0] = i2;
                hullNext[i2] = hullPrev[i1] = i0;

                hullTri[i0] = 0;
                hullTri[i1] = 1;
                hullTri[i2] = 2;

                hullHash[utils.hashkey(p0, c, hashSize)] = i0;
                hullHash[utils.hashkey(p1, c, hashSize)] = i1;
                hullHash[utils.hashkey(p2, c, hashSize)] = i2;

                // Add the initial triangle
                AddTriangle(i0, i1, i2, -1, -1, -1);

                for (var k = 0; k < ids.Length; k++)
                {
                    var i = ids[k];
                    if (i == i0 || i == i1 || i == i2) continue;

                    var p = positions[i];

                    // Find a visible edge on the convex hull using edge hash
                    var start = 0;
                    var key = utils.hashkey(p, c, hashSize);
                    for (var j = 0; j < hashSize; j++)
                    {
                        start = hullHash[(key + j) % hashSize];
                        if (start != -1 && start != hullNext[start]) break;
                    }

                    start = hullPrev[start];
                    var e = start;
                    var q = hullNext[e];

                    while (!utils.less(Orient2dFast(p, positions[e], positions[q]), utils.ZeroTBig()))
                    {
                        e = q;
                        if (e == start)
                        {
                            e = int.MaxValue;
                            break;
                        }

                        q = hullNext[e];
                    }

                    if (e == int.MaxValue) continue;

                    // Add the first triangle from the point
                    var t = AddTriangle(e, i, hullNext[e], -1, -1, hullTri[e]);

                    // Recursively flip triangles from the point until they satisfy the Delaunay condition
                    hullTri[i] = Legalize(t + 2);
                    // Keep track of boundary triangles on the hull
                    hullTri[e] = t;

                    var next = hullNext[e];
                    q = hullNext[next];

                    // Walk forward through the hull, adding more triangles and flipping recursively
                    while (utils.less(Orient2dFast(p, positions[next], positions[q]), utils.ZeroTBig()))
                    {
                        t = AddTriangle(next, i, q, hullTri[i], -1, hullTri[next]);
                        hullTri[i] = Legalize(t + 2);
                        hullNext[next] = next;
                        next = q;

                        q = hullNext[next];
                    }

                    // Walk backward from the other side, adding more triangles and flipping
                    if (e == start)
                    {
                        q = hullPrev[e];

                        while (utils.less(Orient2dFast(p, positions[q], positions[e]), utils.ZeroTBig()))
                        {
                            t = AddTriangle(q, i, e, -1, hullTri[e], hullTri[q]);
                            Legalize(t + 2);
                            hullTri[q] = t;
                            hullNext[e] = e; // mark as removed
                            e = q;
                            q = hullPrev[e];
                        }
                    }

                    // Update the hull indices
                    hullStart = hullPrev[i] = e;
                    hullNext[e] = hullPrev[next] = i;
                    hullNext[i] = next;

                    // Save the two new edges in the hash table
                    hullHash[utils.hashkey(p, c, hashSize)] = i;
                    hullHash[utils.hashkey(positions[e], c, hashSize)] = e;
                }

                // Trim lists to their actual size
                triangles.Length = trianglesLen;
                halfedges.Length = trianglesLen;
                constrainedHalfedges.Length = trianglesLen;

                ids.Dispose();
                dists.Dispose();
            }

            private int Legalize(int a)
            {
                var stackSize = 0;
                int ar;

                // recursion eliminated with a fixed-size stack
                while (true)
                {
                    var b = halfedges[a];
                    /* if the pair of triangles doesn't satisfy the Delaunay condition
					 * (p1 is inside the circumcircle of [p0, pl, pr]), flip them,
					 * then do the same check/flip recursively for the new pair of triangles
					 *
					 *           pl                    pl
					 *          /||\                  /  \
					 *       al/ || \bl            al/    \a
					 *        /  ||  \              /      \
					 *       /  a||b  \    flip    /___ar___\
					 *     p0\   ||   /p1   =>   p0\---bl---/p1
					 *        \  ||  /              \      /
					 *       ar\ || /br             b\    /br
					 *          \||/                  \  /
					 *           pr                    pr
					 */

                    int a0 = a - a % 3;
                    ar = a0 + (a + 2) % 3;

                    // Check if we are on a convex hull edge
                    if (b == -1)
                    {
                        if (stackSize == 0) break;
                        a = EDGE_STACK[--stackSize];
                        continue;
                    }

                    var b0 = b - b % 3;
                    var al = a0 + (a + 1) % 3;
                    var bl = b0 + (b + 2) % 3;

                    var p0 = triangles[ar];
                    var pr = triangles[a];
                    var pl = triangles[al];
                    var p1 = triangles[bl];

                    var illegal = utils.InCircle(positions[p0], positions[pr], positions[pl], positions[p1]);

                    if (illegal)
                    {
                        triangles[a] = p1;
                        triangles[b] = p0;

                        var hbl = halfedges[bl];

                        // Edge swapped on the other side of the hull (rare); fix the halfedge reference
                        if (hbl == -1)
                        {
                            var e = hullStart;
                            do
                            {
                                if (hullTri[e] == bl)
                                {
                                    hullTri[e] = a;
                                    break;
                                }
                                e = hullPrev[e];
                            } while (e != hullStart);
                        }
                        Link(a, hbl);
                        Link(b, halfedges[ar]);
                        Link(ar, bl);

                        var br = b0 + (b + 1) % 3;

                        // Don't worry about hitting the cap: it can only happen on extremely degenerate input
                        if (stackSize < EDGE_STACK.Length)
                        {
                            EDGE_STACK[stackSize++] = br;
                        }
                    }
                    else
                    {
                        if (stackSize == 0) break;
                        a = EDGE_STACK[--stackSize];
                    }
                }

                return ar;
            }

            private int AddTriangle(int i0, int i1, int i2, int a, int b, int c)
            {
                var t = trianglesLen;

                triangles[t + 0] = i0;
                triangles[t + 1] = i1;
                triangles[t + 2] = i2;

                Link(t + 0, a);
                Link(t + 1, b);
                Link(t + 2, c);

                trianglesLen += 3;

                return t;
            }

            private void Link(int a, int b)
            {
                halfedges[a] = b;
                if (b != -1) halfedges[b] = a;
            }
        }

        /// <summary>
        /// This step implements <i>Sloan algorithm</i>.
        /// Read more in the paper:
        /// <see href="https://doi.org/10.1016/0045-7949(93)90239-A">
        /// S. W. Sloan. "A fast algorithm for generating constrained Delaunay triangulations." <i>Comput. Struct.</i> <b>47</b>.3:441-450 (1993).
        /// </see>
        /// </summary>
        private struct ConstrainEdgesStep
        {
            private NativeReference<Status> status;
            private NativeArray<T2>.ReadOnly positions;
            private NativeArray<int> triangles;
            private NativeArray<int>.ReadOnly inputConstraintEdges;
            private NativeArray<ConstraintType>.ReadOnly inputConstraintEdgeTypes;
            /// <summary>
            /// There are 3 halfedges per triangle. For a triangle ABC, the halfedges are A->B, B->C, and C->A.
            ///
            /// For each halfedge index, the value is the index of the opposite halfedge in the adjacent triangle,
            /// or -1 if the halfedge is on the convex hull of the triangulation.
            /// </summary>
            // NOTE: `halfedges` and `constrainedHalfedges` can be NativeArray, however, Job system can throw here the exception:
            //
            // ```
            // InvalidOperationException: The Unity.Collections.NativeList`1[System.Int32]
            // has been declared as [WriteOnly] in the job, but you are reading from it.
            // ```
            //
            // See the `UsingTempAllocatorInJobTest` to learn more.
            private NativeList<int> halfedges;
            private NativeList<HalfedgeState> constrainedHalfedges;
            private readonly Args args;

            private NativeList<int> intersections;
            private NativeList<int> unresolvedIntersections;

            /// <summary>
            /// Maps vertex indices to halfedge indices.
            /// If a vertex is part of multiple halfedges, an arbitrary representative halfedge is stored.
            /// </summary>
            private NativeArray<int> pointToHalfedge;

            public ConstrainEdgesStep(InputData<T2> input, OutputData<T2> output, Args args)
            {
                status = output.Status;
                positions = output.Positions.AsReadOnly();
                triangles = output.Triangles.AsArray();
                inputConstraintEdges = input.ConstraintEdges.AsReadOnly();
                inputConstraintEdgeTypes = input.ConstraintEdgeTypes.AsReadOnly();
                halfedges = output.Halfedges;
                constrainedHalfedges = output.ConstrainedHalfedges;
                this.args = args;

                intersections = default;
                unresolvedIntersections = default;
                pointToHalfedge = default;
            }

            public void Execute(Allocator allocator)
            {
                if (!inputConstraintEdges.IsCreated || status.Value.IsError)
                {
                    return;
                }

                using var _ = Markers.ConstrainEdgesStep.Auto();

                using var _intersections = intersections = new NativeList<int>(allocator);
                using var _unresolvedIntersections = unresolvedIntersections = new NativeList<int>(allocator);
                using var _pointToHalfedge = pointToHalfedge = new NativeArray<int>(positions.Length, allocator);

                // build point to halfedge
                for (int i = 0; i < triangles.Length; i++)
                {
                    pointToHalfedge[triangles[i]] = i;
                }

                for (int index = 0; index < inputConstraintEdges.Length / 2; index++)
                {
                    var c = math.int2(
                        inputConstraintEdges[2 * index + 0],
                        inputConstraintEdges[2 * index + 1]
                        );
                    c = c.x < c.y ? c.xy : c.yx; // Backward compatibility. To remove in the future.
                    var type = inputConstraintEdgeTypes.IsCreated ? inputConstraintEdgeTypes[index] : ConstraintType.ConstrainedAndHoleBoundary;
                    TryApplyConstraint(c, type == ConstraintType.Constrained ? HalfedgeState.Constrained : HalfedgeState.ConstrainedAndHoleBoundary);
                }
            }

            private void TryResolveIntersections(int2 c, HalfedgeState constrainValue, ref int iter)
            {
                for (int i = 0; i < intersections.Length; i++)
                {
                    if (IsMaxItersExceeded(iter++, args.SloanMaxIters))
                    {
                        return;
                    }

                    //  p                             i
                    //      h2 -----------> h0   h4
                    //      ^             .'   .^:
                    //      :           .'   .'  :
                    //      :         .'   .'    :
                    //      :       .'   .'      :
                    //      :     .'   .'        :
                    //      :   .'   .'          :
                    //      : v'   .'            v
                    //      h1   h3 <----------- h5
                    // j                              q
                    //
                    //  p                             i
                    //      h2   h3 -----------> h4
                    //      ^ '.   ^.            :
                    //      :   '.   '.          :
                    //      :     '.   '.        :
                    //      :       '.   '.      :
                    //      :         '.   '.    :
                    //      :           '.   '.  :
                    //      :             'v   '.v
                    //      h1 <----------- h0   h5
                    // j                              q
                    //
                    // Changes:
                    // ---------------------------------------------
                    //              h0   h1   h2   |   h3   h4   h5
                    // ---------------------------------------------
                    // triangles     i    j    p   |    j    i    q
                    // triangles'   *q*   j    p   |   *p*   i    q
                    // ---------------------------------------------
                    // halfedges    h3   g1   g2   |   h0   f1   f2
                    // halfedges'  *h5'* g1  *h5*  |  *h2'* f1  *h2*, where hi' = halfedge[hi]
                    // ---------------------------------------------
                    // intersec.'    X    X   h3   |    X    X   h0
                    // ---------------------------------------------

                    var h0 = intersections[i];
                    var h1 = NextHalfedge(h0);
                    var h2 = NextHalfedge(h1);

                    var h3 = halfedges[h0];
                    var h4 = NextHalfedge(h3);
                    var h5 = NextHalfedge(h4);

                    var _i = triangles[h0];
                    var _j = triangles[h1];
                    var _p = triangles[h2];
                    var _q = triangles[h5];

                    var (p0, p1, p2, p3) = (positions[_i], positions[_q], positions[_j], positions[_p]);
                    if (!IsConvexQuadrilateral(p0, p1, p2, p3))
                    {
                        unresolvedIntersections.Add(h0);
                        continue;
                    }

                    // Swap edge (see figure above)
                    triangles[h0] = _q;
                    triangles[h3] = _p;
                    pointToHalfedge[_q] = h0;
                    pointToHalfedge[_p] = h3;
                    pointToHalfedge[_i] = h4;
                    pointToHalfedge[_j] = h1;
                    ReplaceHalfedge(h5, h0);
                    ReplaceHalfedge(h2, h3);
                    halfedges[h2] = h5;
                    halfedges[h5] = h2;
                    constrainedHalfedges[h2] = HalfedgeState.Unconstrained;
                    constrainedHalfedges[h5] = HalfedgeState.Unconstrained;

                    // Fix intersections
                    for (int j = i + 1; j < intersections.Length; j++)
                    {
                        var tmp = intersections[j];
                        intersections[j] = tmp == h2 ? h3 : tmp == h5 ? h0 : tmp;
                    }
                    for (int j = 0; j < unresolvedIntersections.Length; j++)
                    {
                        var tmp = unresolvedIntersections[j];
                        unresolvedIntersections[j] = tmp == h2 ? h3 : tmp == h5 ? h0 : tmp;
                    }

                    var swapped = math.int2(_p, _q);
                    if (math.all(c.xy == swapped.xy) || math.all(c.xy == swapped.yx))
                    {
                        constrainedHalfedges[h2] = constrainValue;
                        constrainedHalfedges[h5] = constrainValue;
                    }
                    if (EdgeEdgeIntersection(c, swapped))
                    {
                        unresolvedIntersections.Add(h2);
                    }
                }

                intersections.Clear();
            }

            /// <summary>
            /// Replaces <paramref name="h0"/> with <paramref name="h1"/>.
            /// </summary>
            private void ReplaceHalfedge(int h0, int h1)
            {
                var h0p = halfedges[h0];
                halfedges[h1] = h0p;
                constrainedHalfedges[h1] = constrainedHalfedges[h0];

                if (h0p != -1)
                {
                    halfedges[h0p] = h1;
                    constrainedHalfedges[h0p] = constrainedHalfedges[h0];
                }
            }

            private bool EdgeEdgeIntersection(int2 e1, int2 e2)
            {
                var (a0, a1) = (positions[e1.x], positions[e1.y]);
                var (b0, b1) = (positions[e2.x], positions[e2.y]);
                return !(math.any(e1.xy == e2.xy | e1.xy == e2.yx)) && UnsafeTriangulator<T, T2, TBig, TTransform, TUtils>.EdgeEdgeIntersection(a0, a1, b0, b1);
            }

            void MarkHalfedgeConstrained(int halfedge, HalfedgeState constrainValue)
            {
                // If two constraints overlap, the one which is more constrained is used
                constrainValue = (HalfedgeState)math.max((int)constrainedHalfedges[halfedge], (int)constrainValue);
                constrainedHalfedges[halfedge] = constrainValue;
                var oh = halfedges[halfedge];
                if (oh != -1)
                {
                    constrainedHalfedges[oh] = constrainValue;
                }
            }

            private void TryApplyConstraint(int2 edge, HalfedgeState constrainValue)
            {
                intersections.Clear();
                unresolvedIntersections.Clear();

                // We start at the vertex ci=edge.x,
                // then we walk in a straight line through the triangulated mesh
                // until we reach the vertex cj=edge.y.
                // Along the way, we collect all halfedges that intersect with the edge ci-cj
                // and add them as unresolved intersections.
                //
                // If we find a vertex k (not ci or cj) that lies exactly on the edge ci-cj,
                // then the constraint cannot be satisfied as is, and we instead split the
                // constraint into two: ci-k and k-cj. This can happen multiple times.

                // 1. Check if h1 is cj
                // 2. Check if h1-h2 intersects with ci-cj
                // 3. After each iteration: h0 <- h0'
                //
                //          h1
                //       .^ |
                //     .'   |
                //   .'     v
                // h0 <---- h2
                // h0'----> h1'
                //   ^.     |
                //     '.   |
                //       '. v
                //          h2'
                var tunnelInit = -1;
                var (ci, cj) = (edge.x, edge.y);
                var h0init = pointToHalfedge[ci];
                var h0 = h0init;
                do
                {
                    var h1 = NextHalfedge(h0);
                    if (triangles[h1] == cj)
                    {
                        MarkHalfedgeConstrained(h0, constrainValue);
                        break;
                    }
                    var h2 = NextHalfedge(h1);

                    if (PointLineSegmentIntersection(positions[triangles[h1]], positions[ci], positions[cj]))
                    {
                        // h1 lies on the edge ci-cj, split the constraint
                        // Note: h1 and h2 cannot both lie on the constraint, since that would mean that the triangle h0,h1,h2 is degenerate
                        MarkHalfedgeConstrained(h0, constrainValue);
                        cj = triangles[h1];
                        break;
                    }
                    if (PointLineSegmentIntersection(positions[triangles[h2]], positions[ci], positions[cj]) && triangles[h2] != cj)
                    {
                        // h2 lies on the edge ci-cj, split the constraint
                        MarkHalfedgeConstrained(h2, constrainValue);
                        cj = triangles[h2];
                        break;
                    }
                    if (EdgeEdgeIntersection(new int2(ci, cj), new(triangles[h1], triangles[h2])))
                    {
                        unresolvedIntersections.Add(h1);
                        tunnelInit = halfedges[h1];
                        break;
                    }

                    h0 = halfedges[h2];

                    // Boundary reached check other side
                    if (h0 == -1)
                    {
                        if (triangles[h2] == cj)
                        {
                            MarkHalfedgeConstrained(h2, constrainValue);
                        }

                        // possible that triangles[h2] == cj, not need to check
                        break;
                    }
                } while (h0 != h0init);

                h0 = halfedges[h0init];
                if (tunnelInit == -1 && h0 != -1)
                {
                    h0 = NextHalfedge(h0);
                    // Same but reversed
                    do
                    {
                        var h1 = NextHalfedge(h0);
                        if (triangles[h1] == cj)
                        {
                            MarkHalfedgeConstrained(h0, constrainValue);
                            break;
                        }
                        var h2 = NextHalfedge(h1);

                        if (PointLineSegmentIntersection(positions[triangles[h1]], positions[ci], positions[cj]))
                        {
                            // h1 lies on the edge ci-cj, split the constraint
                            MarkHalfedgeConstrained(h0, constrainValue);
                            cj = triangles[h1];
                            break;
                        }
                        if (PointLineSegmentIntersection(positions[triangles[h2]], positions[ci], positions[cj]) && triangles[h2] != cj)
                        {
                            // h2 lies on the edge ci-cj, split the constraint
                            MarkHalfedgeConstrained(h2, constrainValue);
                            cj = triangles[h2];
                            break;
                        }
                        if (EdgeEdgeIntersection(new int2(ci, cj), new(triangles[h1], triangles[h2])))
                        {
                            unresolvedIntersections.Add(h1);
                            tunnelInit = halfedges[h1];
                            break;
                        }

                        h0 = halfedges[h0];
                        // Boundary reached
                        if (h0 == -1)
                        {
                            break;
                        }
                        h0 = NextHalfedge(h0);
                    } while (h0 != h0init);
                }

                // Tunnel algorithm
                // At this point we know that the segment ci->cj enters the triangle h0-h1-h2 via the edge h0-h1.
                // In each iteration we have three options:
                // * The segment ci-cj exits the triangle via one of its other edges,
                //   in which case we follow to a the next triangle,
                // * h2 is cj, in which case we are done.
                // * h2 lies on the segment ci-cj, in which case we split the constraint as above.
                //
                // h2'
                //  ^'.
                //  |  '.
                //  |    'v
                // h1'<-- h0'
                // h1 --> h2  h1''
                //  ^   .'   ^ |
                //  | .'   .'  |
                //  |v   .'    v
                // h0   h0''<--h2''
                //
                // 1. if h2 == cj break
                // 2. if h1-h2 intersects ci-cj, repeat with h0 <- halfedges[h1] = h0'
                // 3. if h2-h0 intersects ci-cj, repeat with h0 <- halfedges[h2] = h0''
                while (tunnelInit != -1)
                {
                    var h0p = tunnelInit;
                    tunnelInit = -1;
                    var h1p = NextHalfedge(h0p);
                    var h2p = NextHalfedge(h1p);

                    if (triangles[h2p] == cj)
                    {
                        break;
                    }
                    if (PointLineSegmentIntersection(positions[triangles[h2p]], positions[ci], positions[cj]))
                    {
                        cj = triangles[h2p];
                        break;
                    }
                    else if (EdgeEdgeIntersection(new int2(ci, cj), new(triangles[h1p], triangles[h2p])))
                    {
                        unresolvedIntersections.Add(h1p);
                        tunnelInit = halfedges[h1p];
                    }
                    else if (EdgeEdgeIntersection(new int2(ci, cj), new(triangles[h2p], triangles[h0p])))
                    {
                        unresolvedIntersections.Add(h2p);
                        tunnelInit = halfedges[h2p];
                    }
                }

                var iter = 0;
                do
                {
                    if (status.Value.IsError)
                    {
                        return;
                    }

                    (intersections, unresolvedIntersections) = (unresolvedIntersections, intersections);
                    TryResolveIntersections(new int2(ci, cj), constrainValue, ref iter);
                } while (!unresolvedIntersections.IsEmpty);

                // If the constraint was split, continue with the remaining part
                if (edge.y != cj)
                {
                    TryApplyConstraint(new int2(cj, edge.y), constrainValue);
                }
            }

            private bool IsMaxItersExceeded(int iter, int maxIters)
            {
                if (iter >= maxIters)
                {
                    status.Value = Status.SloanMaxItersExceeded;
                    return true;
                }
                return false;
            }
        }

        private struct PlantingSeedStep
        {
            private NativeReference<Status> status;
            private NativeList<int> triangles;
            [ReadOnly]
            private NativeList<T2> positions;
            private NativeList<HalfedgeState> constrainedHalfedges;
            private NativeList<int> halfedges;
            private NativeArray<bool> shouldRemoveTriangle;
            private NativeQueue<int> trianglesQueue;
            private NativeArray<T2> holes;
            private bool anyRemovedTriangles;

            private readonly Args args;

            public PlantingSeedStep(InputData<T2> input, OutputData<T2> output, Args args) : this(output, args, input.HoleSeeds) { }

            public PlantingSeedStep(OutputData<T2> output, Args args, NativeArray<T2> localHoles)
            {
                status = output.Status;
                triangles = output.Triangles;
                positions = output.Positions;
                constrainedHalfedges = output.ConstrainedHalfedges;
                halfedges = output.Halfedges;
                holes = localHoles;
                this.args = args;

                shouldRemoveTriangle = default;
                trianglesQueue = default;
                anyRemovedTriangles = false;
            }

            public void Execute(Allocator allocator, bool constraintsIsCreated)
            {
                if (!constraintsIsCreated || status.IsCreated && status.Value.IsError)
                {
                    return;
                }

                using var _ = Markers.PlantingSeedStep.Auto();

                using var _shouldRemoveTriangle = shouldRemoveTriangle = new(triangles.Length / 3, allocator);

                if (args.AutoHolesAndBoundary) PlantAuto(allocator);
                if (holes.IsCreated || args.RestoreBoundary)
                {
                    trianglesQueue = new(allocator);
                    if (holes.IsCreated) PlantHoleSeeds(holes);
                    if (args.RestoreBoundary) PlantBoundarySeeds();
                    trianglesQueue.Dispose();
                }

                RemoveVisitedTriangles(allocator);
            }

            private void PlantBoundarySeeds()
            {
                for (int he = 0; he < halfedges.Length; he++)
                {
                    if (halfedges[he] == -1 &&
                        !shouldRemoveTriangle[he / 3] &&
                        constrainedHalfedges[he] != HalfedgeState.ConstrainedAndHoleBoundary)
                    {
                        PlantSeed(he / 3);
                    }
                }
            }

            private void PlantHoleSeeds(NativeArray<T2> holeSeeds)
            {
                foreach (var s in holeSeeds)
                {
                    var tId = FindTriangle(s);
                    if (tId != -1)
                    {
                        PlantSeed(tId);
                    }
                }
            }


            private void RemoveVisitedTriangles(Allocator allocator)
            {
                if (!anyRemovedTriangles)
                {
                    return;
                }

                // Indices to remove are marked with -1, otherwise they are assigned with incremental id.
                var indexRemap = new NativeArray<int>(triangles.Length / 3, allocator);
                var count = 0;
                for (int tId = 0; tId < shouldRemoveTriangle.Length; tId++)
                {
                    indexRemap[tId] = shouldRemoveTriangle[tId] ? -1 : count++;
                }

                int RemapHalfedge(int he)
                {
                    if (he == -1)
                    {
                        return -1;
                    }
                    var newIndex = indexRemap[he / 3];
                    return newIndex == -1 ? -1 : 3 * newIndex + he % 3;
                }

                // Reinterpret to a larger struct to make copies of whole triangles slightly more efficient
                var constrainedHalfedges3 = constrainedHalfedges.AsArray().Reinterpret<bool3>(1);
                var triangles3 = triangles.AsArray().Reinterpret<int3>(4);

                // Copy the triangles, constrained halfedges, and halfedges to new indices in-place.
                for (int tId = 0; tId < indexRemap.Length; tId++)
                {
                    var tIdNew = indexRemap[tId];
                    if (tIdNew != -1)
                    {
                        triangles3[tIdNew] = triangles3[tId];
                        constrainedHalfedges3[tIdNew] = constrainedHalfedges3[tId];
                        halfedges[3 * tIdNew + 0] = RemapHalfedge(halfedges[3 * tId + 0]);
                        halfedges[3 * tIdNew + 1] = RemapHalfedge(halfedges[3 * tId + 1]);
                        halfedges[3 * tIdNew + 2] = RemapHalfedge(halfedges[3 * tId + 2]);
                    }
                }

                // Trim the data to reflect removed triangles.
                triangles.Length = 3 * count;
                constrainedHalfedges.Length = 3 * count;
                halfedges.Length = 3 * count;

                indexRemap.Dispose();
            }

            private void PlantSeed(int tId)
            {
                var shouldRemoveTriangle = this.shouldRemoveTriangle;
                var trianglesQueue = this.trianglesQueue;

                if (shouldRemoveTriangle[tId])
                {
                    return;
                }

                shouldRemoveTriangle[tId] = true;
                trianglesQueue.Enqueue(tId);
                anyRemovedTriangles = true;

                // Search outwards from the seed triangle and mark all triangles
                // until we get to a hole boundary, or a previously visited triangle.
                while (trianglesQueue.TryDequeue(out tId))
                {
                    for (int i = 0; i < 3; i++)
                    {
                        var he = 3 * tId + i;
                        var ohe = halfedges[he];
                        if (constrainedHalfedges[he] == HalfedgeState.ConstrainedAndHoleBoundary || ohe == -1)
                        {
                            continue;
                        }

                        var otherId = ohe / 3;
                        if (!shouldRemoveTriangle[otherId])
                        {
                            shouldRemoveTriangle[otherId] = true;
                            trianglesQueue.Enqueue(otherId);
                        }
                    }
                }
            }

            private int FindTriangle(T2 p)
            {
                for (int tId = 0; tId < triangles.Length / 3; tId++)
                {
                    var (i, j, k) = (triangles[3 * tId + 0], triangles[3 * tId + 1], triangles[3 * tId + 2]);
                    var (a, b, c) = (positions[i], positions[j], positions[k]);
                    if (utils.PointInsideTriangle(p, a, b, c))
                    {
                        return tId;
                    }
                }

                return -1;
            }

            private void PlantAuto(Allocator allocator)
            {
                var triCount = triangles.Length / 3;
                var queue = new NativeQueue<int>(allocator);
                var nextQueue = new NativeQueue<int>(allocator);
                var visitedTriangles = new NativeArray<bool>(triCount, allocator);

                // Start at the boundary of the triangulation
                for (int tId = 0; tId < triCount; tId++)
                {
                    for (int i = 0; i < 3; i++)
                    {
                        if (halfedges[3 * tId + i] == -1)
                        {
                            (constrainedHalfedges[3 * tId + i] == HalfedgeState.ConstrainedAndHoleBoundary ? nextQueue : queue).Enqueue(tId);
                            visitedTriangles[tId] = true;
                            break;
                        }
                    }
                }

                // Search inwards from the boundary
                // When crossing a hole bondary, we flip between removing triangles and keeping triangles
                // This effectively implements the EvenOdd fill mode (https://en.wikipedia.org/wiki/Even%E2%80%93odd_rule).
                bool anyRemovedTriangles = false;
                for (bool remove = true; !queue.IsEmpty() || !nextQueue.IsEmpty(); remove = !remove)
                {
                    while (queue.TryDequeue(out var tId))
                    {
                        if (remove)
                        {
                            shouldRemoveTriangle[tId] = true;
                            anyRemovedTriangles = true;
                        }

                        for (int i = 0; i < 3; i++)
                        {
                            var he = 3 * tId + i;
                            var ohe = halfedges[he];
                            var oTri = ohe / 3;
                            if (ohe != -1 && !visitedTriangles[oTri])
                            {
                                (constrainedHalfedges[he] == HalfedgeState.ConstrainedAndHoleBoundary ? nextQueue : queue).Enqueue(oTri);
                                visitedTriangles[oTri] = true;
                            }
                        }
                    }

                    (queue, nextQueue) = (nextQueue, queue);
                }

                this.anyRemovedTriangles = anyRemovedTriangles;
                queue.Dispose();
                nextQueue.Dispose();
                visitedTriangles.Dispose();
            }
        }

        private struct RefineMeshStep
        {
            private readonly struct Circle
            {
                public readonly T2 Center;
                public readonly T RadiusSq;
                public Circle((T2 center, T radiusSq) circle) => (Center, RadiusSq) = (circle.center, circle.radiusSq);
            }

            private NativeReference<Status> status;
            private NativeList<int> triangles;
            private NativeList<T2> outputPositions;
            private NativeList<int> halfedges;
            private NativeList<HalfedgeState> constrainedHalfedges;

            private NativeList<Circle> circles;
            private NativeQueue<int> trianglesQueue;
            private NativeList<int> badTriangles;
            private NativeList<int> pathPoints;
            private NativeList<int> pathHalfedges;
            private NativeList<bool> visitedTriangles;

            private readonly T maximumArea2, angleThreshold;
            private readonly int initialPointsCount;

            /// <summary>
            /// A parameter for the concentric shells edge splitting algorithm.
            /// This is a pretty arbitrary constant. Changing it has a minor effect on the triangulation, but only in rare situations.
            /// Pretty much any value is as good as any other.
            ///
            /// See `Delaunay Refinement Algorithm for Quality 2-Dimensional Mesh Generation` page 40.
            /// </summary>
            const float ConcentricShellReferenceRadius = 0.001f;

            public RefineMeshStep(OutputData<T2> output, Args args, TTransform lt) : this(output,
                                                                                          area2Threshold: utils.Cast(utils.mul(utils.Cast(utils.mul(utils.Const(2), utils.Const(args.RefinementThresholdArea))), lt.AreaScalingFactor)),
                                                                                          angleThreshold: utils.Const(args.RefinementThresholdAngle))
            { }

            public RefineMeshStep(OutputData<T2> output, T area2Threshold, T angleThreshold)
            {
                status = output.Status;
                initialPointsCount = output.Positions.Length;
                maximumArea2 = area2Threshold;
                this.angleThreshold = angleThreshold;
                triangles = output.Triangles;
                outputPositions = output.Positions;
                halfedges = output.Halfedges;
                constrainedHalfedges = output.ConstrainedHalfedges;

                circles = default;
                trianglesQueue = default;
                badTriangles = default;
                pathPoints = default;
                pathHalfedges = default;
                visitedTriangles = default;
            }

            public void Execute(Allocator allocator, bool refineMesh, bool constrainBoundary)
            {
                if (!refineMesh || status.IsCreated && status.Value.IsError)
                {
                    return;
                }

                using var _ = Markers.RefineMeshStep.Auto();

                if (!utils.SupportsRefinement())
                {
                    status.Value = Status.IntegersDoNotSupportMeshRefinement;
                    return;
                }

                if (constrainBoundary)
                {
                    for (int he = 0; he < constrainedHalfedges.Length; he++)
                    {
                        constrainedHalfedges[he] = halfedges[he] == -1 ? HalfedgeState.ConstrainedAndHoleBoundary : HalfedgeState.Unconstrained;
                    }
                }

                using var _circles = circles = new(allocator) { Length = triangles.Length / 3 };
                using var _trianglesQueue = trianglesQueue = new(allocator);
                using var _badTriangles = badTriangles = new(triangles.Length / 3, allocator);
                using var _pathPoints = pathPoints = new(allocator);
                using var _pathHalfedges = pathHalfedges = new(allocator);
                using var _visitedTriangles = visitedTriangles = new(triangles.Length / 3, allocator);

                using var heQueue = new NativeList<int>(triangles.Length, allocator);
                using var tQueue = new NativeList<int>(triangles.Length, allocator);

                for (int tId = 0; tId < triangles.Length / 3; tId++)
                {
                    var (i, j, k) = (triangles[3 * tId + 0], triangles[3 * tId + 1], triangles[3 * tId + 2]);
                    circles[tId] = new(CalculateCircumCircle(i, j, k, outputPositions.AsArray()));
                }

                // Collect encroached half-edges.
                for (int he = 0; he < constrainedHalfedges.Length; he++)
                {
                    if (constrainedHalfedges[he] >= HalfedgeState.Constrained && IsEncroached(he))
                    {
                        heQueue.Add(he);
                    }
                }

                SplitEncroachedEdges(heQueue, tQueue: default); // ignore bad triangles in this run

                // Collect encroached triangles
                for (int tId = 0; tId < triangles.Length / 3; tId++)
                {
                    if (IsBadTriangle(tId))
                    {
                        tQueue.Add(tId);
                    }
                }

                // Split triangles
                for (int i = 0; i < tQueue.Length; i++)
                {
                    var tId = tQueue[i];
                    if (tId != -1)
                    {
                        SplitTriangle(tId, heQueue, tQueue, allocator);
                    }
                }
            }

            private void SplitEncroachedEdges(NativeList<int> heQueue, NativeList<int> tQueue)
            {
                for (int i = 0; i < heQueue.Length; i++)
                {
                    var he = heQueue[i];
                    if (he != -1)
                    {
                        SplitEdge(he, heQueue, tQueue);
                    }
                }
                heQueue.Clear();
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private bool IsEncroached(int he0)
            {
                var he1 = NextHalfedge(he0);
                var he2 = NextHalfedge(he1);

                var p0 = outputPositions[triangles[he0]];
                var p1 = outputPositions[triangles[he1]];
                var p2 = outputPositions[triangles[he2]];

                return utils.le(utils.dot(utils.diff(p0, p2), utils.diff(p1, p2)), utils.Zero());
            }

            private void SplitEdge(int he, NativeList<int> heQueue, NativeList<int> tQueue)
            {
                var (i, j) = (triangles[he], triangles[NextHalfedge(he)]);
                var (e0, e1) = (outputPositions[i], outputPositions[j]);

                T2 p;
                // Use midpoint method for:
                // - the first segment split,
                // - subsegment not made of input vertices.
                // Otherwise, use "concentric circular shells".
                if (i < initialPointsCount && j < initialPointsCount ||
                    i >= initialPointsCount && j >= initialPointsCount)
                {
                    p = utils.avg(e0, e1);
                }
                else
                {
                    var alpha = utils.alpha(utils.Const(ConcentricShellReferenceRadius), dSquare: utils.Cast(utils.distancesq(e0, e1)));
                    // Swap points to provide symmetry in splitting
                    p = i < initialPointsCount ? utils.lerp(e0, e1, alpha) : utils.lerp(e1, e0, alpha);
                }

                constrainedHalfedges[he] = HalfedgeState.Unconstrained;
                var ohe = halfedges[he];
                if (ohe != -1)
                {
                    constrainedHalfedges[ohe] = HalfedgeState.Unconstrained;
                }

                if (halfedges[he] != -1)
                {
                    UnsafeInsertPointBulk(p, initTriangle: he / 3, heQueue, tQueue);

                    var h0 = triangles.Length - 3;
                    var hi = -1;
                    var hj = -1;
                    while (hi == -1 || hj == -1)
                    {
                        var h1 = NextHalfedge(h0);
                        if (triangles[h1] == i)
                        {
                            hi = h0;
                        }
                        if (triangles[h1] == j)
                        {
                            hj = h0;
                        }

                        var h2 = NextHalfedge(h1);
                        h0 = halfedges[h2];
                    }

                    if (IsEncroached(hi))
                    {
                        heQueue.Add(hi);
                    }
                    var ohi = halfedges[hi];
                    if (IsEncroached(ohi))
                    {
                        heQueue.Add(ohi);
                    }
                    if (IsEncroached(hj))
                    {
                        heQueue.Add(hj);
                    }
                    var ohj = halfedges[hj];
                    if (IsEncroached(ohj))
                    {
                        heQueue.Add(ohj);
                    }

                    constrainedHalfedges[hi] = HalfedgeState.Constrained;
                    constrainedHalfedges[ohi] = HalfedgeState.Constrained;
                    constrainedHalfedges[hj] = HalfedgeState.Constrained;
                    constrainedHalfedges[ohj] = HalfedgeState.Constrained;
                }
                else
                {
                    UnsafeInsertPointBoundary(p, initHe: he, heQueue, tQueue);

                    //var h0 = triangles.Length - 3;
                    var id = 3 * (pathPoints.Length - 1);
                    var hi = halfedges.Length - 1;
                    var hj = halfedges.Length - id;

                    if (IsEncroached(hi))
                    {
                        heQueue.Add(hi);
                    }

                    if (IsEncroached(hj))
                    {
                        heQueue.Add(hj);
                    }

                    constrainedHalfedges[hi] = HalfedgeState.Constrained;
                    constrainedHalfedges[hj] = HalfedgeState.Constrained;
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private bool IsBadTriangle(int tId)
            {
                var (i, j, k) = (triangles[3 * tId + 0], triangles[3 * tId + 1], triangles[3 * tId + 2]);
                var (a, b, c) = (outputPositions[i], outputPositions[j], outputPositions[k]);
                var area2 = Area2(a, b, c);
                return utils.greater(area2, maximumArea2) || AngleIsTooSmall(tId, angleThreshold);
            }

            private void SplitTriangle(int tId, NativeList<int> heQueue, NativeList<int> tQueue, Allocator allocator)
            {
                var c = circles[tId];
                var edges = new NativeList<int>(allocator);

                for (int he = 0; he < constrainedHalfedges.Length; he++)
                {
                    if (constrainedHalfedges[he] == HalfedgeState.Unconstrained)
                    {
                        continue;
                    }

                    var (i, j) = (triangles[he], triangles[NextHalfedge(he)]);
                    if (halfedges[he] == -1 || i < j)
                    {
                        var (p0, p1) = (outputPositions[i], outputPositions[j]);
                        if (utils.le(utils.dot(utils.diff(p0, c.Center), utils.diff(p1, c.Center)), utils.Zero()))
                        {
                            edges.Add(he);
                        }
                    }
                }

                if (edges.IsEmpty)
                {
                    UnsafeInsertPointBulk(c.Center, initTriangle: tId, heQueue, tQueue);
                }
                else
                {
                    var (i, j, k) = (triangles[3 * tId + 0], triangles[3 * tId + 1], triangles[3 * tId + 2]);
                    var (xi, xj, xk) = (outputPositions[i], outputPositions[j], outputPositions[k]);
                    var area2 = Area2(xi, xj, xk);
                    if (utils.greater(area2, maximumArea2))
                    { // TODO split permited
                        foreach (var he in edges.AsReadOnly())
                        {
                            heQueue.Add(he);
                        }
                    }
                    if (!heQueue.IsEmpty)
                    {
                        tQueue.Add(tId);
                        SplitEncroachedEdges(heQueue, tQueue);
                    }
                }

                edges.Dispose();
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private bool AngleIsTooSmall(int tId, T minimumAngle)
            {
                var (i, j, k) = (triangles[3 * tId + 0], triangles[3 * tId + 1], triangles[3 * tId + 2]);
                var (pA, pB, pC) = (outputPositions[i], outputPositions[j], outputPositions[k]);
                return UnsafeTriangulator<T, T2, TBig, TTransform, TUtils>.AngleIsTooSmall(pA, pB, pC, minimumAngle);
            }

            private int UnsafeInsertPointCommon(T2 p, int initTriangle)
            {
                var pId = outputPositions.Length;
                outputPositions.Add(p);

                badTriangles.Clear();
                trianglesQueue.Clear();
                pathPoints.Clear();
                pathHalfedges.Clear();

                visitedTriangles.Clear();
                visitedTriangles.Length = triangles.Length / 3;

                trianglesQueue.Enqueue(initTriangle);
                badTriangles.Add(initTriangle);
                visitedTriangles[initTriangle] = true;
                RecalculateBadTriangles(p);

                return pId;
            }

            private void UnsafeInsertPointBulk(T2 p, int initTriangle, NativeList<int> heQueue = default, NativeList<int> tQueue = default)
            {
                var pId = UnsafeInsertPointCommon(p, initTriangle);
                BuildStarPolygon();
                ProcessBadTriangles(heQueue, tQueue);
                BuildNewTrianglesForStar(pId, heQueue, tQueue);
            }

            private void UnsafeInsertPointBoundary(T2 p, int initHe, NativeList<int> heQueue = default, NativeList<int> tQueue = default)
            {
                var pId = UnsafeInsertPointCommon(p, initHe / 3);
                BuildAmphitheaterPolygon(initHe);
                ProcessBadTriangles(heQueue, tQueue);
                BuildNewTrianglesForAmphitheater(pId, heQueue, tQueue);
            }

            private void RecalculateBadTriangles(T2 p)
            {
                while (trianglesQueue.TryDequeue(out var tId))
                {
                    VisitEdge(p, 3 * tId + 0);
                    VisitEdge(p, 3 * tId + 1);
                    VisitEdge(p, 3 * tId + 2);
                }
            }

            private void VisitEdge(T2 p, int t0)
            {
                var he = halfedges[t0];
                if (he == -1 || constrainedHalfedges[he] >= HalfedgeState.Constrained)
                {
                    return;
                }

                var otherId = he / 3;
                if (visitedTriangles[otherId])
                {
                    return;
                }

                var circle = circles[otherId];
                if (utils.le(utils.Cast(utils.distancesq(circle.Center, p)), circle.RadiusSq))
                {
                    badTriangles.Add(otherId);
                    trianglesQueue.Enqueue(otherId);
                    visitedTriangles[otherId] = true;
                }
            }

            private void BuildAmphitheaterPolygon(int initHe)
            {
                var id = initHe;
                var initPoint = triangles[id];
                while (true)
                {
                    id = NextHalfedge(id);
                    if (triangles[id] == initPoint)
                    {
                        break;
                    }

                    var he = halfedges[id];
                    if (he == -1 || !badTriangles.Contains(he / 3))
                    {
                        pathPoints.Add(triangles[id]);
                        pathHalfedges.Add(he);
                        continue;
                    }
                    id = he;
                }
                pathPoints.Add(triangles[initHe]);
                pathHalfedges.Add(-1);
            }

            private void BuildStarPolygon()
            {
                // Find the "first" halfedge of the polygon.
                var initHe = -1;
                for (int i = 0; i < badTriangles.Length; i++)
                {
                    var tId = badTriangles[i];
                    for (int t = 0; t < 3; t++)
                    {
                        var he = 3 * tId + t;
                        var ohe = halfedges[he];
                        if (ohe == -1 || !badTriangles.Contains(ohe / 3))
                        {
                            pathPoints.Add(triangles[he]);
                            pathHalfedges.Add(ohe);
                            initHe = he;
                            break;
                        }
                    }
                    if (initHe != -1)
                    {
                        break;
                    }
                }

                // Build polygon path from halfedges and points.
                var id = initHe;
                var initPoint = pathPoints[0];
                while (true)
                {
                    id = NextHalfedge(id);
                    if (triangles[id] == initPoint)
                    {
                        break;
                    }

                    var he = halfedges[id];
                    if (he == -1 || !badTriangles.Contains(he / 3))
                    {
                        pathPoints.Add(triangles[id]);
                        pathHalfedges.Add(he);
                        continue;
                    }
                    id = he;
                }
            }

            private void ProcessBadTriangles(NativeList<int> heQueue, NativeList<int> tQueue)
            {
                // Remove bad triangles and recalculate polygon path halfedges.
                badTriangles.Sort();
                for (int t = badTriangles.Length - 1; t >= 0; t--)
                {
                    var tId = badTriangles[t];
                    triangles.RemoveAt(3 * tId + 2);
                    triangles.RemoveAt(3 * tId + 1);
                    triangles.RemoveAt(3 * tId + 0);
                    circles.RemoveAt(tId);
                    RemoveHalfedge(3 * tId + 2, 0);
                    RemoveHalfedge(3 * tId + 1, 1);
                    RemoveHalfedge(3 * tId + 0, 2);
                    constrainedHalfedges.RemoveAt(3 * tId + 2);
                    constrainedHalfedges.RemoveAt(3 * tId + 1);
                    constrainedHalfedges.RemoveAt(3 * tId + 0);

                    for (int i = 3 * tId; i < halfedges.Length; i++)
                    {
                        var he = halfedges[i];
                        if (he == -1)
                        {
                            continue;
                        }
                        halfedges[he < 3 * tId ? he : i] -= 3;
                    }

                    for (int i = 0; i < pathHalfedges.Length; i++)
                    {
                        if (pathHalfedges[i] > 3 * tId + 2)
                        {
                            pathHalfedges[i] -= 3;
                        }
                    }

                    // Adapt he queue
                    if (heQueue.IsCreated)
                    {
                        for (int i = 0; i < heQueue.Length; i++)
                        {
                            var he = heQueue[i];
                            if (he == 3 * tId + 0 || he == 3 * tId + 1 || he == 3 * tId + 2)
                            {
                                heQueue[i] = -1;
                                continue;
                            }

                            if (he > 3 * tId + 2)
                            {
                                heQueue[i] -= 3;
                            }
                        }
                    }

                    // Adapt t queue
                    if (tQueue.IsCreated)
                    {
                        for (int i = 0; i < tQueue.Length; i++)
                        {
                            var q = tQueue[i];
                            if (q == tId)
                            {
                                tQueue[i] = -1;
                                continue;
                            }

                            if (q > tId)
                            {
                                tQueue[i]--;
                            }
                        }
                    }
                }
            }

            private void RemoveHalfedge(int he, int offset)
            {
                var ohe = halfedges[he];
                var o = ohe > he ? ohe - offset : ohe;
                if (o > -1)
                {
                    halfedges[o] = -1;
                }
                halfedges.RemoveAt(he);
            }

            private void BuildNewTrianglesForStar(int pId, NativeList<int> heQueue, NativeList<int> tQueue)
            {
                // Build triangles/circles for inserted point pId.
                var initTriangles = triangles.Length;
                triangles.Length += 3 * pathPoints.Length;
                circles.Length += pathPoints.Length;
                for (int i = 0; i < pathPoints.Length - 1; i++)
                {
                    triangles[initTriangles + 3 * i + 0] = pId;
                    triangles[initTriangles + 3 * i + 1] = pathPoints[i];
                    triangles[initTriangles + 3 * i + 2] = pathPoints[i + 1];
                    circles[initTriangles / 3 + i] = new(CalculateCircumCircle(pId, pathPoints[i], pathPoints[i + 1], outputPositions.AsArray()));
                }
                triangles[^3] = pId;
                triangles[^2] = pathPoints[^1];
                triangles[^1] = pathPoints[0];
                circles[^1] = new(CalculateCircumCircle(pId, pathPoints[^1], pathPoints[0], outputPositions.AsArray()));

                // Build half-edges for inserted point pId.
                var heOffset = halfedges.Length;
                halfedges.Length += 3 * pathPoints.Length;
                constrainedHalfedges.Length += 3 * pathPoints.Length;
                for (int i = 0; i < pathPoints.Length - 1; i++)
                {
                    var he = pathHalfedges[i];
                    halfedges[3 * i + 1 + heOffset] = he;
                    if (he != -1)
                    {
                        halfedges[he] = 3 * i + 1 + heOffset;
                        constrainedHalfedges[3 * i + 1 + heOffset] = constrainedHalfedges[he];
                    }
                    else
                    {
                        constrainedHalfedges[3 * i + 1 + heOffset] = HalfedgeState.Constrained;
                    }
                    halfedges[3 * i + 2 + heOffset] = 3 * i + 3 + heOffset;
                    halfedges[3 * i + 3 + heOffset] = 3 * i + 2 + heOffset;
                }
                var phe = pathHalfedges[^1];
                halfedges[heOffset + 3 * (pathPoints.Length - 1) + 1] = phe;
                if (phe != -1)
                {
                    halfedges[phe] = heOffset + 3 * (pathPoints.Length - 1) + 1;
                    constrainedHalfedges[heOffset + 3 * (pathPoints.Length - 1) + 1] = constrainedHalfedges[phe];
                }
                else
                {
                    constrainedHalfedges[heOffset + 3 * (pathPoints.Length - 1) + 1] = HalfedgeState.Constrained;
                }
                halfedges[heOffset] = heOffset + 3 * (pathPoints.Length - 1) + 2;
                halfedges[heOffset + 3 * (pathPoints.Length - 1) + 2] = heOffset;

                if (heQueue.IsCreated)
                {
                    for (int i = 0; i < pathPoints.Length - 1; i++)
                    {
                        var he = heOffset + 3 * i + 1;
                        if (constrainedHalfedges[he] >= HalfedgeState.Constrained && IsEncroached(he))
                        {
                            heQueue.Add(he);
                        }
                        else if (tQueue.IsCreated && IsBadTriangle(he / 3))
                        {
                            tQueue.Add(he / 3);
                        }
                    }
                }
            }

            private void BuildNewTrianglesForAmphitheater(int pId, NativeList<int> heQueue, NativeList<int> tQueue)
            {
                // Build triangles/circles for inserted point pId.
                var initTriangles = triangles.Length;
                triangles.Length += 3 * (pathPoints.Length - 1);
                circles.Length += pathPoints.Length - 1;
                for (int i = 0; i < pathPoints.Length - 1; i++)
                {
                    triangles[initTriangles + 3 * i + 0] = pId;
                    triangles[initTriangles + 3 * i + 1] = pathPoints[i];
                    triangles[initTriangles + 3 * i + 2] = pathPoints[i + 1];
                    circles[initTriangles / 3 + i] = new(CalculateCircumCircle(pId, pathPoints[i], pathPoints[i + 1], outputPositions.AsArray()));
                }

                // Build half-edges for inserted point pId.
                var heOffset = halfedges.Length;
                halfedges.Length += 3 * (pathPoints.Length - 1);
                constrainedHalfedges.Length += 3 * (pathPoints.Length - 1);
                for (int i = 0; i < pathPoints.Length - 2; i++)
                {
                    var he = pathHalfedges[i];
                    halfedges[3 * i + 1 + heOffset] = he;
                    if (he != -1)
                    {
                        halfedges[he] = 3 * i + 1 + heOffset;
                        constrainedHalfedges[3 * i + 1 + heOffset] = constrainedHalfedges[he];
                    }
                    else
                    {
                        constrainedHalfedges[3 * i + 1 + heOffset] = HalfedgeState.Constrained;
                    }
                    halfedges[3 * i + 2 + heOffset] = 3 * i + 3 + heOffset;
                    halfedges[3 * i + 3 + heOffset] = 3 * i + 2 + heOffset;
                }

                var phe = pathHalfedges[^2];
                halfedges[heOffset + 3 * (pathPoints.Length - 2) + 1] = phe;
                if (phe != -1)
                {
                    halfedges[phe] = heOffset + 3 * (pathPoints.Length - 2) + 1;
                    constrainedHalfedges[heOffset + 3 * (pathPoints.Length - 2) + 1] = constrainedHalfedges[phe];
                }
                else
                {
                    constrainedHalfedges[heOffset + 3 * (pathPoints.Length - 2) + 1] = HalfedgeState.Constrained;
                }
                halfedges[heOffset] = -1;
                halfedges[heOffset + 3 * (pathPoints.Length - 2) + 2] = -1;

                if (heQueue.IsCreated)
                {
                    for (int i = 0; i < pathPoints.Length - 1; i++)
                    {
                        var he = heOffset + 3 * i + 1;
                        if (constrainedHalfedges[he] >= HalfedgeState.Constrained && IsEncroached(he))
                        {
                            heQueue.Add(he);
                        }
                        else if (tQueue.IsCreated && IsBadTriangle(he / 3))
                        {
                            tQueue.Add(he / 3);
                        }
                    }
                }
            }
        }

        internal static bool AngleIsTooSmall(T2 pA, T2 pB, T2 pC, T minimumAngle)
        {
            // Implementation is based in dot product property:
            //    a·b = |a| |b| cos α
            var threshold = utils.cos(minimumAngle);

            var pAB = utils.normalizesafe(utils.diff(pB, pA));
            var pBC = utils.normalizesafe(utils.diff(pC, pB));
            var pCA = utils.normalizesafe(utils.diff(pA, pC));

            return utils.anygreaterthan(
                utils.dot(pAB, utils.neg(pCA)),
                utils.dot(pBC, utils.neg(pAB)),
                utils.dot(pCA, utils.neg(pBC)),
                threshold
                );
        }
        internal static T Area2(T2 a, T2 b, T2 c) => utils.abs(Cross(utils.diff(b, a), utils.diff(c, a)));
        private static T Cross(T2 a, T2 b) => utils.Cast(utils.diff(utils.mul(utils.X(a), utils.Y(b)), utils.mul(utils.Y(a), utils.X(b))));
        private static TBig CircumRadiusSq(T2 a, T2 b, T2 c) => utils.distancesq(utils.CircumCenter(a, b, c), a);
        private static (T2, T) CalculateCircumCircle(int i, int j, int k, NativeArray<T2> positions)
        {
            var (pA, pB, pC) = (positions[i], positions[j], positions[k]);
            return (utils.CircumCenter(pA, pB, pC), utils.Cast(CircumRadiusSq(pA, pB, pC)));
        }
        private static bool ccw(T2 a, T2 b, T2 c) => utils.greater(
            utils.mul(utils.diff(utils.Y(c), utils.Y(a)), utils.diff(utils.X(b), utils.X(a))),
            utils.mul(utils.diff(utils.Y(b), utils.Y(a)), utils.diff(utils.X(c), utils.X(a)))
            );
        /// <summary>
        /// Returns <see langword="true"/> if edge (<paramref name="a0"/>, <paramref name="a1"/>) intersects
        /// (<paramref name="b0"/>, <paramref name="b1"/>), <see langword="false"/> otherwise.
        /// </summary>
        /// <remarks>
        /// This method will not catch intersecting collinear segments. See unit tests for more details.
        /// Segments intersecting only at their endpoints may or may not return <see langword="true"/>, depending on their orientation.
        /// </remarks>
        internal static bool EdgeEdgeIntersection(T2 a0, T2 a1, T2 b0, T2 b1) => ccw(a0, a1, b0) != ccw(a0, a1, b1) && ccw(b0, b1, a0) != ccw(b0, b1, a1);

        private static int NextHalfedge(int he) => he % 3 == 2 ? he - 2 : he + 1;
        internal static bool IsConvexQuadrilateral(T2 a, T2 b, T2 c, T2 d) => true
        && utils.greater(utils.abs(Orient2dFast(a, c, b)), utils.EPSILON())
        && utils.greater(utils.abs(Orient2dFast(a, c, d)), utils.EPSILON())
        && utils.greater(utils.abs(Orient2dFast(b, d, a)), utils.EPSILON())
        && utils.greater(utils.abs(Orient2dFast(b, d, c)), utils.EPSILON())
        && EdgeEdgeIntersection(a, c, b, d)
        ;
        private static TBig Orient2dFast(T2 a, T2 b, T2 c) => utils.diff(
            utils.mul(utils.diff(utils.Y(a), utils.Y(c)), utils.diff(utils.X(b), utils.X(c))),
            utils.mul(utils.diff(utils.X(a), utils.X(c)), utils.diff(utils.Y(b), utils.Y(c)))
            );
        internal static bool PointLineSegmentIntersection(T2 a, T2 b0, T2 b1) => true
        && utils.le(utils.abs(Orient2dFast(a, b0, b1)), utils.EPSILON())
        && math.all(utils.ge(a, utils.min(b0, b1)) & utils.le(a, utils.max(b0, b1)));
    }

    internal interface ITransform<TSelf, T, T2> where T : unmanaged where T2 : unmanaged
    {
        T AreaScalingFactor { get; }
        TSelf Identity { get; }
        TSelf Inverse();
        T2 Transform(T2 point);
        /// <summary>
        /// Returns PCA transformation of given <paramref name="positions"/>.
        /// Read more in the project manual.
        /// </summary>
        TSelf CalculatePCATransformation(NativeArray<T2> positions);
        /// <summary>
        /// Returns COM transformation of given <paramref name="positions"/>.
        /// Read more in the project manual about method restrictions for given type.
        /// </summary>
        TSelf CalculateLocalTransformation(NativeArray<T2> positions);
    }

    internal readonly struct TransformFloat : ITransform<TransformFloat, float, float2>
    {
        public readonly TransformFloat Identity => new(float2x2.identity, float2.zero);
        public readonly float AreaScalingFactor => math.abs(math.determinant(rotScale));

        private readonly float2x2 rotScale;
        private readonly float2 translation;

        public TransformFloat(float2x2 rotScale, float2 translation) => (this.rotScale, this.translation) = (rotScale, translation);
        private static TransformFloat Translate(float2 offset) => new(float2x2.identity, offset);
        private static TransformFloat Scale(float2 scale) => new(new float2x2(scale.x, 0, 0, scale.y), float2.zero);
        private static TransformFloat Rotate(float2x2 rotation) => new(rotation, float2.zero);
        public static TransformFloat operator *(TransformFloat lhs, TransformFloat rhs) => new(
            math.mul(lhs.rotScale, rhs.rotScale),
            math.mul(math.inverse(rhs.rotScale), lhs.translation) + rhs.translation
            );

        public TransformFloat Inverse() => new(math.inverse(rotScale), math.mul(rotScale, -translation));
        public float2 Transform(float2 point) => math.mul(rotScale, point + translation);

        public readonly TransformFloat CalculatePCATransformation(NativeArray<float2> positions)
        {
            var com = (float2)0;
            foreach (var p in positions)
            {
                com += p;
            }
            com /= positions.Length;

            var cov = float2x2.zero;
            for (int i = 0; i < positions.Length; i++)
            {
                var q = positions[i] - com;
                cov += Kron(q, q);
            }
            cov /= positions.Length;

            Eigen(cov, out _, out var rotationMatrix);

            var partialTransform = Rotate(math.transpose(rotationMatrix)) * Translate(-com);
            float2 min = float.MaxValue;
            float2 max = float.MinValue;
            for (int i = 0; i < positions.Length; i++)
            {
                var p = partialTransform.Transform(positions[i]);
                min = math.min(p, min);
                max = math.max(p, max);
            }

            var c = 0.5f * (min + max);
            var s = 2f / (max - min);

            return Scale(s) * Translate(-c) * partialTransform;
        }

        public readonly TransformFloat CalculateLocalTransformation(NativeArray<float2> positions)
        {
            float2 min = float.MaxValue, max = float.MinValue, com = 0;
            foreach (var p in positions)
            {
                min = math.min(p, min);
                max = math.max(p, max);
                com += p;
            }

            com /= positions.Length;
            var scale = 1 / math.cmax(math.max(math.abs(max - com), math.abs(min - com)));
            return Scale(scale) * Translate(-com);
        }

        /// <summary>
        /// Solves <see href="https://en.wikipedia.org/wiki/Eigenvalues_and_eigenvectors">eigen problem</see> of the given <paramref name="matrix"/>.
        /// </summary>
        /// <param name="eigval">Eigen values.</param>
        /// <param name="eigvec">Eigen vectors.</param>
        private static void Eigen(float2x2 matrix, out float2 eigval, out float2x2 eigvec)
        {
            var a00 = matrix[0][0];
            var a11 = matrix[1][1];
            var a01 = matrix[0][1];

            var a00a11 = a00 - a11;
            var p1 = a00 + a11;
            var p2 = (a00a11 >= 0 ? 1 : -1) * math.sqrt(a00a11 * a00a11 + 4 * a01 * a01);
            var lambda1 = p1 + p2;
            var lambda2 = p1 - p2;
            eigval = 0.5f * math.float2(lambda1, lambda2);

            var phi = 0.5f * math.atan2(2 * a01, a00a11);

            eigvec = math.float2x2
                     (
                m00: math.cos(phi), m01: -math.sin(phi),
                m10: math.sin(phi), m11: math.cos(phi)
                     );
        }

        /// <summary>
        /// Returns <see href="https://en.wikipedia.org/wiki/Kronecker_product">Kronecer product</see> of <paramref name="a"/> and <paramref name="b"/>.
        /// </summary>
        private static float2x2 Kron(float2 a, float2 b) => math.float2x2(a * b[0], a * b[1]);
    }

    internal readonly struct TransformDouble : ITransform<TransformDouble, double, double2>
    {
        public readonly TransformDouble Identity => new(double2x2.identity, double2.zero);
        public readonly double AreaScalingFactor => math.abs(math.determinant(rotScale));

        private readonly double2x2 rotScale;
        private readonly double2 translation;

        public TransformDouble(double2x2 rotScale, double2 translation) => (this.rotScale, this.translation) = (rotScale, translation);
        private static TransformDouble Translate(double2 offset) => new(double2x2.identity, offset);
        private static TransformDouble Scale(double2 scale) => new(new double2x2(scale.x, 0, 0, scale.y), double2.zero);
        private static TransformDouble Rotate(double2x2 rotation) => new(rotation, double2.zero);
        public static TransformDouble operator *(TransformDouble lhs, TransformDouble rhs) => new(
            math.mul(lhs.rotScale, rhs.rotScale),
            math.mul(math.inverse(rhs.rotScale), lhs.translation) + rhs.translation
            );

        public TransformDouble Inverse() => new(math.inverse(rotScale), math.mul(rotScale, -translation));
        public double2 Transform(double2 point) => math.mul(rotScale, point + translation);

        public readonly TransformDouble CalculatePCATransformation(NativeArray<double2> positions)
        {
            var com = (double2)0;
            foreach (var p in positions)
            {
                com += p;
            }
            com /= positions.Length;

            var cov = double2x2.zero;
            for (int i = 0; i < positions.Length; i++)
            {
                var q = positions[i] - com;
                cov += Kron(q, q);
            }
            cov /= positions.Length;

            Eigen(cov, out _, out var rotationMatrix);

            var partialTransform = Rotate(math.transpose(rotationMatrix)) * Translate(-com);
            double2 min = double.MaxValue;
            double2 max = double.MinValue;
            for (int i = 0; i < positions.Length; i++)
            {
                var p = partialTransform.Transform(positions[i]);
                min = math.min(p, min);
                max = math.max(p, max);
            }

            var c = 0.5f * (min + max);
            var s = 2f / (max - min);

            return Scale(s) * Translate(-c) * partialTransform;
        }

        public readonly TransformDouble CalculateLocalTransformation(NativeArray<double2> positions)
        {
            double2 min = double.MaxValue, max = double.MinValue, com = 0;
            foreach (var p in positions)
            {
                min = math.min(p, min);
                max = math.max(p, max);
                com += p;
            }

            com /= positions.Length;
            var scale = 1 / math.cmax(math.max(math.abs(max - com), math.abs(min - com)));
            return Scale(scale) * Translate(-com);
        }

        /// <summary>
        /// Solves <see href="https://en.wikipedia.org/wiki/Eigenvalues_and_eigenvectors">eigen problem</see> of the given <paramref name="matrix"/>.
        /// </summary>
        /// <param name="eigval">Eigen values.</param>
        /// <param name="eigvec">Eigen vectors.</param>
        private static void Eigen(double2x2 matrix, out double2 eigval, out double2x2 eigvec)
        {
            var a00 = matrix[0][0];
            var a11 = matrix[1][1];
            var a01 = matrix[0][1];

            var a00a11 = a00 - a11;
            var p1 = a00 + a11;
            var p2 = (a00a11 >= 0 ? 1 : -1) * math.sqrt(a00a11 * a00a11 + 4 * a01 * a01);
            var lambda1 = p1 + p2;
            var lambda2 = p1 - p2;
            eigval = 0.5f * math.double2(lambda1, lambda2);

            var phi = 0.5f * math.atan2(2 * a01, a00a11);

            eigvec = math.double2x2
                     (
                m00: math.cos(phi), m01: -math.sin(phi),
                m10: math.sin(phi), m11: math.cos(phi)
                     );
        }

        /// <summary>
        /// Returns <see href="https://en.wikipedia.org/wiki/Kronecker_product">Kronecer product</see> of <paramref name="a"/> and <paramref name="b"/>.
        /// </summary>
        private static double2x2 Kron(double2 a, double2 b) => math.double2x2(a * b[0], a * b[1]);
    }

    /// <summary>
    /// <b>Note:</b> translation transformation is only supported for type <see cref="int2"/>.
    /// </summary>
    internal readonly struct TransformInt : ITransform<TransformInt, int, int2>
    {
        public readonly TransformInt Identity => new(int2.zero);
        public readonly int AreaScalingFactor => 1;
        private readonly int2 translation;
        public TransformInt(int2 translation) => this.translation = translation;
        public TransformInt Inverse() => new(-translation);
        public int2 Transform(int2 point) => point + translation;
        public readonly TransformInt CalculatePCATransformation(NativeArray<int2> positions) => throw new NotImplementedException(
            "PCA is not implemented for int2 coordinates!"
            );

        public readonly TransformInt CalculateLocalTransformation(NativeArray<int2> positions)
        {
            int2 min = int.MaxValue, max = int.MinValue, com = 0;
            foreach (var p in positions)
            {
                min = math.min(p, min);
                max = math.max(p, max);
                com += p;
            }

            return new(-com / positions.Length);
        }
    }

#if UNITY_MATHEMATICS_FIXEDPOINT
	internal readonly struct TransformFp : ITransform<TransformFp, fp, fp2> {
		// NOTE: fpmath misses determinant and inverse functions.
		private static fp det(fp2x2 m) => m[0][0] * m[1][1] - m[0][1] * m[1][0];
		private static fp2x2 inv(fp2x2 m) => fpmath.fp2x2(m[1][1], -m[1][0], -m[0][1], m[0][0]) / det(m);
		public readonly TransformFp Identity => new(fp2x2.identity, fp2.zero);
		public readonly fp AreaScalingFactor => fpmath.abs(det(rotScale));

		private readonly fp2x2 rotScale;
		private readonly fp2 translation;

		public TransformFp(fp2x2 rotScale, fp2 translation) => (this.rotScale, this.translation) = (rotScale, translation);
		private static TransformFp Translate(fp2 offset) => new(fp2x2.identity, offset);
		private static TransformFp Scale(fp2 scale) => new(new fp2x2(scale.x, 0, 0, scale.y), fp2.zero);
		private static TransformFp Rotate(fp2x2 rotation) => new(rotation, fp2.zero);
		public static TransformFp operator *(TransformFp lhs, TransformFp rhs) => new(
			fpmath.mul(lhs.rotScale, rhs.rotScale),
			fpmath.mul(inv(rhs.rotScale), lhs.translation) + rhs.translation
			);

		public TransformFp Inverse() => new(inv(rotScale), fpmath.mul(rotScale, -translation));
		public fp2 Transform(fp2 point) => fpmath.mul(rotScale, point + translation);

		public readonly TransformFp CalculatePCATransformation (NativeArray<fp2> positions) {
			var com = (fp2)0;
			foreach (var p in positions) {
				com += p;
			}
			com /= positions.Length;

			var cov = fp2x2.zero;
			for (int i = 0; i < positions.Length; i++) {
				var q = positions[i] - com;
				cov += Kron(q, q);
			}
			cov /= positions.Length;

			Eigen(cov, out _, out var rotationMatrix);

			var partialTransform = Rotate(fpmath.transpose(rotationMatrix)) * Translate(-com);
			fp2 min = fp.max_value;
			fp2 max = fp.min_value;
			for (int i = 0; i < positions.Length; i++) {
				var p = partialTransform.Transform(positions[i]);
				min = fpmath.min(p, min);
				max = fpmath.max(p, max);
			}

			var c = (min + max) / 2;
			var s = (fp)2L / (max - min);

			return Scale(s) * Translate(-c) * partialTransform;
		}

		public readonly TransformFp CalculateLocalTransformation (NativeArray<fp2> positions) {
			fp2 min = fp.max_value, max = fp.min_value, com = fp2.zero;
			foreach (var p in positions) {
				min = fpmath.min(p, min);
				max = fpmath.max(p, max);
				com += p;
			}

			com /= positions.Length;
			var scale = 1 / fpmath.cmax(fpmath.max(fpmath.abs(max - com), fpmath.abs(min - com)));
			return Scale(scale) * Translate(-com);
		}

		/// <summary>
		/// Solves <see href="https://en.wikipedia.org/wiki/Eigenvalues_and_eigenvectors">eigen problem</see> of the given <paramref name="matrix"/>.
		/// </summary>
		/// <param name="eigval">Eigen values.</param>
		/// <param name="eigvec">Eigen vectors.</param>
		private static void Eigen (fp2x2 matrix, out fp2 eigval, out fp2x2 eigvec) {
			var a00 = matrix[0][0];
			var a11 = matrix[1][1];
			var a01 = matrix[0][1];

			var a00a11 = a00 - a11;
			var p1 = a00 + a11;
			var p2 = (a00a11 >= 0 ? 1 : -1) * fpmath.sqrt(a00a11 * a00a11 + 4 * a01 * a01);
			var lambda1 = p1 + p2;
			var lambda2 = p1 - p2;
			eigval = fpmath.fp2(lambda1, lambda2) / 2;

			var phi = fpmath.atan2(2 * a01, a00a11) / 2;

			eigvec = fpmath.fp2x2
					 (
				m00: fpmath.cos(phi), m01: -fpmath.sin(phi),
				m10: fpmath.sin(phi), m11: fpmath.cos(phi)
					 );
		}

		/// <summary>
		/// Returns <see href="https://en.wikipedia.org/wiki/Kronecker_product">Kronecer product</see> of <paramref name="a"/> and <paramref name="b"/>.
		/// </summary>
		private static fp2x2 Kron(fp2 a, fp2 b) => fpmath.fp2x2(a * b[0], a * b[1]);
	}
#endif

    /// <typeparam name="T">The raw coordinate type for a single axis. For example <see cref="float"/> or <see cref="int"/>.</typeparam>
    /// <typeparam name="T2">The 2D coordinate composed of Ts. For example <see cref="float2"/>.</typeparam>
    /// <typeparam name="TBig">A value that may have higher precision compared to <typeparamref name="T"/>. Used for squared distances and other products.</typeparam>
    internal interface IUtils<T, T2, TBig> where T : unmanaged where T2 : unmanaged where TBig : unmanaged
    {
        /// <summary>
        /// Cast a float to <typeparamref name="T"/>. Note that for integer coordinates, this will be floored.
        /// <b>Warning!</b> This operation may cause precision loss, use with caution.
        /// </summary>
        T Cast(TBig v);
        T2 CircumCenter(T2 a, T2 b, T2 c);
        T Const(float v);
        TBig EPSILON();
        bool InCircle(T2 a, T2 b, T2 c, T2 p);
        TBig MaxValue();
        T2 MaxValue2();
        T2 MinValue2();
        bool PointInsideTriangle(T2 p, T2 a, T2 b, T2 c);
        bool SupportsRefinement();
        T X(T2 v);
        T Y(T2 v);
        T Zero();
        TBig ZeroTBig();
#pragma warning disable IDE1006
        T abs(T v);
        TBig abs(TBig v);
        /// <summary>
        /// Returns concentric shells segment splitting factor.
        /// </summary>
        /// <param name="concentricShellReferenceRadius">Concentric shells parameter constant.</param>
        /// <param name="dSquare">Segment length squared.</param>
        /// <returns><i>alpha</i> in [0, 1] range.</returns>
        /// <remarks>
        /// Learn more in the paper:
        /// <see href="https://doi.org/10.1006/jagm.1995.1021">
        /// J. Ruppert. "A Delaunay Refinement Algorithm for Quality 2-Dimensional Mesh Generation". <i>J. Algorithms</i> <b>18</b>(3):548-585 (1995)
        /// </see>.
        /// </remarks>
        T alpha(T concentricShellReferenceRadius, T dSquare);
        bool anygreaterthan(T a, T b, T c, T v);
        T2 avg(T2 a, T2 b);
        T cos(T v);
        T diff(T a, T b);
        TBig diff(TBig a, TBig b);
        T2 diff(T2 a, T2 b);
        TBig distancesq(T2 a, T2 b);
        T dot(T2 a, T2 b);
        bool2 eq(T2 v, T2 w);
        bool2 ge(T2 a, T2 b);
        bool greater(T a, T b);
        bool greater(TBig a, TBig b);
        int hashkey(T2 p, T2 c, int hashSize);
        bool2 isfinite(T2 v);
        bool le(T a, T b);
        bool le(TBig a, TBig b);
        bool2 le(T2 a, T2 b);
        T2 lerp(T2 a, T2 b, T v);
        bool less(TBig a, TBig b);
        T2 max(T2 v, T2 w);
        T2 min(T2 v, T2 w);
        TBig mul(T a, T b);
        T2 neg(T2 v);
        T2 normalizesafe(T2 v);
#pragma warning restore IDE1006
    }

    internal readonly struct FloatUtils : IUtils<float, float2, float>
    {
        public readonly float Cast(float v) => v;
        public readonly float2 CircumCenter(float2 a, float2 b, float2 c)
        {
            var d = b - a;
            var e = c - a;

            var bl = math.lengthsq(d);
            var cl = math.lengthsq(e);

            var d2 = 0.5f / (d.x * e.y - d.y * e.x);

            return a + d2 * (bl * math.float2(e.y, -e.x) + cl * math.float2(-d.y, d.x));
        }
        public readonly float Const(float v) => v;
        public readonly float EPSILON() => math.EPSILON;
        public readonly bool InCircle(float2 a, float2 b, float2 c, float2 p)
        {
            var dx = a.x - p.x;
            var dy = a.y - p.y;
            var ex = b.x - p.x;
            var ey = b.y - p.y;
            var fx = c.x - p.x;
            var fy = c.y - p.y;

            var ap = dx * dx + dy * dy;
            var bp = ex * ex + ey * ey;
            var cp = fx * fx + fy * fy;

            return dx * (ey * cp - bp * fy) - dy * (ex * cp - bp * fx) + ap * (ex * fy - ey * fx) < 0;
        }
        public readonly float MaxValue() => float.MaxValue;
        public readonly float2 MaxValue2() => float.MaxValue;
        public readonly float2 MinValue2() => float.MinValue;
        public readonly bool PointInsideTriangle(float2 p, float2 a, float2 b, float2 c)
        {
            static float cross(float2 a, float2 b) => a.x * b.y - a.y * b.x;
            static float3 bar(float2 a, float2 b, float2 c, float2 p)
            {
                var (v0, v1, v2) = (b - a, c - a, p - a);
                var denInv = 1 / cross(v0, v1);
                var v = denInv * cross(v2, v1);
                var w = denInv * cross(v0, v2);
                var u = 1.0f - v - w;
                return new(u, v, w);
            }
            // NOTE: use barycentric property.
            return math.cmax(-bar(a, b, c, p)) <= 0;
        }
        public readonly bool SupportsRefinement() => true;
        public readonly float X(float2 a) => a.x;
        public readonly float Y(float2 a) => a.y;
        public readonly float Zero() => 0;
        public readonly float ZeroTBig() => 0;
        public readonly float abs(float v) => math.abs(v);
        public readonly float alpha(float concentricShellReferenceRadius, float edgeLengthSq)
        {
            var d = math.sqrt(edgeLengthSq);
            var k = (int)math.round(math.log2(0.5f * d / concentricShellReferenceRadius));
            return concentricShellReferenceRadius / d * (k < 0 ? math.pow(2, k) : 1 << k);
        }
        public readonly bool anygreaterthan(float a, float b, float c, float v) => math.any(math.float3(a, b, c) > v);
        public readonly float2 avg(float2 a, float2 b) => 0.5f * (a + b);
        public readonly float cos(float v) => math.cos(v);
        public readonly float diff(float a, float b) => a - b;
        public readonly float2 diff(float2 a, float2 b) => a - b;
        public readonly float distancesq(float2 a, float2 b) => math.distancesq(a, b);
        public readonly float dot(float2 a, float2 b) => math.dot(a, b);
        public readonly bool2 eq(float2 v, float2 w) => v == w;
        public readonly bool2 ge(float2 a, float2 b) => a >= b;
        public readonly bool greater(float a, float b) => a > b;
        public readonly int hashkey(float2 p, float2 c, int hashSize)
        {
            return (int)math.floor(pseudoAngle(p.x - c.x, p.y - c.y) * hashSize) % hashSize;

            static float pseudoAngle(float dx, float dy)
            {
                var p = dx / (math.abs(dx) + math.abs(dy));
                return (dy > 0 ? 3 - p : 1 + p) / 4; // [0..1]
            }
        }
        public readonly bool2 isfinite(float2 v) => math.isfinite(v);
        public readonly bool le(float a, float b) => a <= b;
        public readonly bool2 le(float2 a, float2 b) => a <= b;
        public readonly float2 lerp(float2 a, float2 b, float v) => math.lerp(a, b, v);
        public readonly bool less(float a, float b) => a < b;
        public readonly float2 max(float2 v, float2 w) => math.max(v, w);
        public readonly float2 min(float2 v, float2 w) => math.min(v, w);
        public readonly float mul(float a, float b) => a * b;
        public readonly float2 neg(float2 v) => -v;
        public readonly float2 normalizesafe(float2 v) => math.normalizesafe(v);
    }

    internal readonly struct DoubleUtils : IUtils<double, double2, double>
    {
        public readonly double Cast(double v) => v;
        public readonly double2 CircumCenter(double2 a, double2 b, double2 c)
        {
            var d = b - a;
            var e = c - a;

            var bl = math.lengthsq(d);
            var cl = math.lengthsq(e);

            var d2 = 0.5 / (d.x * e.y - d.y * e.x);

            return a + d2 * (bl * math.double2(e.y, -e.x) + cl * math.double2(-d.y, d.x));
        }
        public readonly double Const(float v) => v;
        public readonly double EPSILON() => math.EPSILON_DBL;
        public readonly bool InCircle(double2 a, double2 b, double2 c, double2 p)
        {
            var dx = a.x - p.x;
            var dy = a.y - p.y;
            var ex = b.x - p.x;
            var ey = b.y - p.y;
            var fx = c.x - p.x;
            var fy = c.y - p.y;

            var ap = dx * dx + dy * dy;
            var bp = ex * ex + ey * ey;
            var cp = fx * fx + fy * fy;

            return dx * (ey * cp - bp * fy) - dy * (ex * cp - bp * fx) + ap * (ex * fy - ey * fx) < 0;
        }
        public readonly double MaxValue() => double.MaxValue;
        public readonly double2 MaxValue2() => double.MaxValue;
        public readonly double2 MinValue2() => double.MinValue;
        public readonly bool PointInsideTriangle(double2 p, double2 a, double2 b, double2 c)
        {
            static double cross(double2 a, double2 b) => a.x * b.y - a.y * b.x;
            static double3 bar(double2 a, double2 b, double2 c, double2 p)
            {
                var (v0, v1, v2) = (b - a, c - a, p - a);
                var denInv = 1 / cross(v0, v1);
                var v = denInv * cross(v2, v1);
                var w = denInv * cross(v0, v2);
                var u = 1 - v - w;
                return new(u, v, w);
            }
            // NOTE: use barycentric property.
            return math.cmax(-bar(a, b, c, p)) <= 0;
        }
        public readonly bool SupportsRefinement() => true;
        public readonly double X(double2 a) => a.x;
        public readonly double Y(double2 a) => a.y;
        public readonly double Zero() => 0;
        public readonly double ZeroTBig() => 0;
        public readonly double abs(double v) => math.abs(v);
        public readonly double alpha(double concentricShellReferenceRadius, double edgeLengthSq)
        {
            var d = math.sqrt(edgeLengthSq);
            var k = (int)math.round(math.log2(0.5 * d / concentricShellReferenceRadius));
            return concentricShellReferenceRadius / d * (k < 0 ? math.pow(2, k) : 1 << k);
        }
        public readonly bool anygreaterthan(double a, double b, double c, double v) => math.any(math.double3(a, b, c) > v);
        public readonly double2 avg(double2 a, double2 b) => 0.5f * (a + b);
        public readonly double cos(double v) => math.cos(v);
        public readonly double diff(double a, double b) => a - b;
        public readonly double2 diff(double2 a, double2 b) => a - b;
        public readonly double distancesq(double2 a, double2 b) => math.distancesq(a, b);
        public readonly double dot(double2 a, double2 b) => math.dot(a, b);
        public readonly bool2 eq(double2 v, double2 w) => v == w;
        public readonly bool2 ge(double2 a, double2 b) => a >= b;
        public readonly bool greater(double a, double b) => a > b;
        public readonly int hashkey(double2 p, double2 c, int hashSize)
        {
            return (int)math.floor(pseudoAngle(p.x - c.x, p.y - c.y) * hashSize) % hashSize;

            static double pseudoAngle(double dx, double dy)
            {
                var p = dx / (math.abs(dx) + math.abs(dy));
                return (dy > 0 ? 3 - p : 1 + p) / 4; // [0..1]
            }
        }
        public readonly bool2 isfinite(double2 v) => math.isfinite(v);
        public readonly bool le(double a, double b) => a <= b;
        public readonly bool2 le(double2 a, double2 b) => a <= b;
        public readonly double2 lerp(double2 a, double2 b, double v) => math.lerp(a, b, v);
        public readonly bool less(double a, double b) => a < b;
        public readonly double2 max(double2 v, double2 w) => math.max(v, w);
        public readonly double2 min(double2 v, double2 w) => math.min(v, w);
        public readonly double mul(double a, double b) => a * b;
        public readonly double2 neg(double2 v) => -v;
        public readonly double2 normalizesafe(double2 v) => math.normalizesafe(v);
    }

    internal readonly struct IntUtils : IUtils<int, int2, long>
    {
        public readonly int Cast(long v) => (int)v;
        public readonly int2 CircumCenter(int2 a, int2 b, int2 c)
        {
            var d = b - a;
            var e = c - a;

            var bl = (long)d.x * d.x + (long)d.y * d.y;
            var cl = (long)e.x * e.x + (long)e.y * e.y;

            var div = (long)d.x * e.y - (long)d.y * e.x;
            // NOTE: In a case when div = 0 (i.e. circumcenter is not well defined) we use int.MaxValue to mimic the infinity.
            //       Doubles can represent all integers up to 2^53 exactly, so they can represent all int32 coordinates, and thus it is safe to cast here.
            return div == 0 ? new(int.MaxValue) : (int2)math.round(a + (0.5 / div) * (bl * math.double2(e.y, -e.x) + cl * math.double2(-d.y, d.x)));
        }

        public readonly int Const(float v) => (int)v;
        public readonly long EPSILON() => 0;

        public readonly bool InCircle(int2 a, int2 b, int2 c, int2 p)
        {
            // Do a coordinate change to check if the origin is inside abc instead.
            // Note: Will overflow if the coordinates differ by more than 2^31 (but this is not the limiting factor)
            a -= p;
            b -= p;
            c -= p;
            // TODO: Is it better for performance to check if the coordinates are small,
            // and if so, do the calculation with 64-bit arithmetic only?

            // Should not overflow since we cast to long
            var ap = (long)a.x * a.x + (long)a.y * a.y;
            var bp = (long)b.x * b.x + (long)b.y * b.y;
            var cp = (long)c.x * c.x + (long)c.y * c.y;

            // This is the calculation we want to do, but it may overflow for large coordinates.
            // Therefore we first do 64-bit multiplications, and then the final 3 multiplications with 128-bit arithmetic.
            // return a.x * (b.y * cp - bp * c.y) - a.y * (b.x * cp - bp * c.x) + ap * (b.x * c.y - b.y * c.x) < 0;

            // May overflow for coordinates larger than about 2^20.
            // Therefore, when verifying coordinates, we ensure that the bounding box is smaller than 2^20.
            var det1 = b.y * cp - bp * c.y;
            var det2 = b.x * cp - bp * c.x;
            var det3 = b.x * (long)c.y - b.y * (long)c.x;

            var res = I128.Multiply(a.x, det1) - I128.Multiply(a.y, det2) + I128.Multiply(ap, det3);

            return res.IsNegative;
        }
        public readonly long MaxValue() => long.MaxValue;
        public readonly int2 MaxValue2() => int.MaxValue;
        public readonly int2 MinValue2() => int.MinValue;
        public readonly bool PointInsideTriangle(int2 p, int2 a, int2 b, int2 c)
        {
            static long cross(int2 a, int2 b) => (long)a.x * b.y - (long)a.y * b.x;
            // NOTE: triangle orientation is guaranteed.
            return cross(p - a, b - a) >= 0 && cross(p - b, c - b) >= 0 && cross(p - c, a - c) >= 0;
        }
        public readonly bool SupportsRefinement() => false;
        public readonly int X(int2 a) => a.x;
        public readonly int Y(int2 a) => a.y;
        public readonly int Zero() => 0;
        public readonly long ZeroTBig() => 0;
        public readonly int abs(int v) => math.abs(v);
        public readonly long abs(long v) => math.abs(v);
        public readonly int alpha(int concentricShellReferenceRadius, int edgeLengthSq) => throw new NotImplementedException();
        public readonly bool anygreaterthan(int a, int b, int c, int v) => throw new NotImplementedException();
        public readonly int2 avg(int2 a, int2 b) => (a + b) / 2;
        public readonly int cos(int v) => throw new NotImplementedException();
        public readonly int diff(int a, int b) => a - b;
        public readonly long diff(long a, long b) => a - b;
        public readonly int2 diff(int2 a, int2 b) => a - b;
        public readonly long distancesq(int2 a, int2 b) => (long)(a - b).x * (a - b).x + (long)(a - b).y * (a - b).y;
        public readonly int dot(int2 a, int2 b) => throw new NotImplementedException();
        public readonly bool2 eq(int2 v, int2 w) => v == w;
        public readonly bool2 ge(int2 a, int2 b) => a >= b;
        public readonly bool greater(int a, int b) => a > b;
        public readonly bool greater(long a, long b) => a > b;
        public readonly int hashkey(int2 p, int2 c, int hashSize)
        {
            return (int)math.floor(pseudoAngle(p.x - c.x, p.y - c.y) * hashSize) % hashSize;

            static double pseudoAngle(int dx, int dy)
            {
                var dist = math.abs(dx) + math.abs(dy);
                if (dist == 0) return 0;
                var p = (double)dx / dist;
                return (dy > 0 ? 3 - p : 1 + p) / 4; // [0..1]
            }
        }
        // TODO: Validate really large coordinates with tests. Probably this should include check for v < 2^20.
        public readonly bool2 isfinite(int2 v) => true;
        public readonly bool le(int a, int b) => a <= b;
        public readonly bool le(long a, long b) => a <= b;
        public readonly bool2 le(int2 a, int2 b) => a <= b;
        public readonly int2 lerp(int2 a, int2 b, int v) => throw new NotImplementedException();
        public readonly bool less(long a, long b) => a < b;
        public readonly int2 max(int2 v, int2 w) => math.max(v, w);
        public readonly int2 min(int2 v, int2 w) => math.min(v, w);
        public readonly long mul(int a, int b) => (long)a * b;
        public readonly int2 neg(int2 v) => -v;
        public readonly int2 normalizesafe(int2 v) => throw new NotImplementedException();
    }

#if UNITY_MATHEMATICS_FIXEDPOINT
	internal readonly struct FpUtils : IUtils<fp, fp2, fp> {
		public readonly fp Cast(fp v) => v;
		public readonly fp2 CircumCenter (fp2 a, fp2 b, fp2 c) {
			var d = b - a;
			var e = c - a;

			var bl = fpmath.lengthsq(d);
			var cl = fpmath.lengthsq(e);

			// NOTE: In a case when div = 0 (i.e. circumcenter is not well defined) we use fp.max_value to mimic the infinity.
			var div = d.x * e.y - d.y * e.x;
			return div == 0 ? fp.max_value : a + (fp)1L / 2L / div * (bl * fpmath.fp2(e.y, -e.x) + cl * fpmath.fp2(-d.y, d.x));
		}
		public readonly fp Const(float v) => (fp)v;
		public readonly fp EPSILON() => fp.FromRaw(1L);
		public readonly bool InCircle (fp2 a, fp2 b, fp2 c, fp2 p) {
			var dx = a.x - p.x;
			var dy = a.y - p.y;
			var ex = b.x - p.x;
			var ey = b.y - p.y;
			var fx = c.x - p.x;
			var fy = c.y - p.y;

			var ap = dx * dx + dy * dy;
			var bp = ex * ex + ey * ey;
			var cp = fx * fx + fy * fy;

			return dx * (ey * cp - bp * fy) - dy * (ex * cp - bp * fx) + ap * (ex * fy - ey * fx) < 0;
		}
		public readonly fp MaxValue() => fp.max_value;
		public readonly fp2 MaxValue2() => fp.max_value;
		public readonly fp2 MinValue2() => fp.min_value;
		public readonly bool PointInsideTriangle (fp2 p, fp2 a, fp2 b, fp2 c) {
			static fp cross(fp2 a, fp2 b) => a.x * b.y - a.y * b.x;
			static fp3 bar (fp2 a, fp2 b, fp2 c, fp2 p) {
				var(v0, v1, v2) = (b - a, c - a, p - a);
				var denInv = 1 / cross(v0, v1);
				var v = denInv * cross(v2, v1);
				var w = denInv * cross(v0, v2);
				var u = (fp)1L - v - w;
				return new(u, v, w);
			}
			// NOTE: use barycentric property.
			return fpmath.cmax(-bar(a, b, c, p)) <= 0;
		}
		public readonly bool SupportRefinement() => true;
		public readonly fp X(fp2 a) => a.x;
		public readonly fp Y(fp2 a) => a.y;
		public readonly fp Zero() => 0;
		public readonly fp ZeroTBig() => 0;
		public readonly fp abs(fp v) => fpmath.abs(v);
		public readonly fp alpha (fp D, fp dSquare) {
			var d = fpmath.sqrt(dSquare);
			var k = (int)fpmath.round(fpmath.log2(d / D / 2L));
			return D / d * (k < 0 ? fpmath.pow(2, k) : 1 << k);
		}
		public readonly bool anygreaterthan(fp a, fp b, fp c, fp v) => math.any(fpmath.fp3(a, b, c) > v);
		public readonly fp2 avg(fp2 a, fp2 b) => (a + b) / 2;
		public readonly fp cos(fp v) => fpmath.cos(v);
		public readonly fp diff(fp a, fp b) => a - b;
		public readonly fp2 diff(fp2 a, fp2 b) => a - b;
		public readonly fp distancesq(fp2 a, fp2 b) => fpmath.distancesq(a, b);
		public readonly fp dot(fp2 a, fp2 b) => fpmath.dot(a, b);
		public readonly bool2 eq(fp2 v, fp2 w) => v == w;
		public readonly bool2 ge(fp2 a, fp2 b) => a >= b;
		public readonly bool greater(fp a, fp b) => a > b;
		public readonly int hashkey (fp2 p, fp2 c, int hashSize) {
			return (int)fpmath.floor(pseudoAngle(p.x - c.x, p.y - c.y) * hashSize) % hashSize;

			static fp pseudoAngle (fp dx, fp dy) {
				var p = dx / (fpmath.abs(dx) + fpmath.abs(dy));
				return (dy > 0 ? 3 - p : 1 + p) / 4; // [0..1]
			}
		}
		public readonly bool2 isfinite(fp2 v) => fpmath.isfinite(v);
		public readonly bool le(fp a, fp b) => a <= b;
		public readonly bool2 le(fp2 a, fp2 b) => a <= b;
		public readonly fp2 lerp(fp2 a, fp2 b, fp v) => fpmath.lerp(a, b, v);
		public readonly bool less(fp a, fp b) => a < b;
		public readonly fp2 max(fp2 v, fp2 w) => fpmath.max(v, w);
		public readonly fp2 min(fp2 v, fp2 w) => fpmath.min(v, w);
		public readonly fp mul(fp a, fp b) => a * b;
		public readonly fp2 neg(fp2 v) => -v;
		public readonly fp2 normalizesafe(fp2 v) => fpmath.normalizesafe(v);
	}
#endif
}
