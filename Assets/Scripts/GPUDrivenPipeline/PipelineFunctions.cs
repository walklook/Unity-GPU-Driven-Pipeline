﻿using System.Collections.Generic;
using UnityEngine;
using Unity.Collections;
using UnityEngine.Rendering;
using Unity.Collections.LowLevel.Unsafe;
using System;
using System.Text;
using System.Runtime.CompilerServices;
using Unity.Mathematics;
using static Unity.Mathematics.math;
using MPipeline;

public unsafe static class PipelineFunctions
{
    public static void RunPostProcess(ref RenderTargets targets, out int source, out int dest)
    {
        source = targets.renderTargetIdentifier;
        dest = targets.backupIdentifier;
        int back = targets.backupIdentifier;
        targets.backupIdentifier = targets.renderTargetIdentifier;
        targets.renderTargetIdentifier = back;
    }

    //TODO: Streaming Loading
    /// <summary>
    /// Initialize pipeline buffers
    /// </summary>
    /// <param name="baseBuffer"></param> pipeline base buffer
    public static void InitBaseBuffer(PipelineBaseBuffer baseBuffer, ClusterMatResources materialResources, string name, int maximumLength)
    {
        baseBuffer.clusterBuffer = new ComputeBuffer(maximumLength, sizeof(CullBox));
        baseBuffer.resultBuffer = new ComputeBuffer(maximumLength, PipelineBaseBuffer.UINTSIZE);
        baseBuffer.instanceCountBuffer = new ComputeBuffer(5, 4, ComputeBufferType.IndirectArguments);
        NativeArray<uint> instanceCountBufferValue = new NativeArray<uint>(5, Allocator.Temp);
        instanceCountBufferValue[0] = PipelineBaseBuffer.CLUSTERVERTEXCOUNT;
        baseBuffer.instanceCountBuffer.SetData(instanceCountBufferValue);
        instanceCountBufferValue.Dispose();
        baseBuffer.verticesBuffer = new ComputeBuffer(maximumLength * PipelineBaseBuffer.CLUSTERCLIPCOUNT, sizeof(Point));
        baseBuffer.clusterCount = 0;
        baseBuffer.dispatchBuffer = new ComputeBuffer(5, 4, ComputeBufferType.IndirectArguments);
        NativeArray<uint> occludedCountList = new NativeArray<uint>(5, Allocator.Temp, NativeArrayOptions.ClearMemory);
        occludedCountList[1] = 1;
        occludedCountList[2] = 1;
        baseBuffer.dispatchBuffer.SetData(occludedCountList);
        baseBuffer.reCheckCount = new ComputeBuffer(5, 4, ComputeBufferType.IndirectArguments);
        baseBuffer.reCheckResult = new ComputeBuffer(maximumLength, 4);
        occludedCountList[0] = PipelineBaseBuffer.CLUSTERVERTEXCOUNT;
        occludedCountList[1] = 0;
        occludedCountList[2] = 0;
        baseBuffer.reCheckCount.SetData(occludedCountList);
        occludedCountList.Dispose();
    }
    /// <summary>
    /// Get Frustum Corners
    /// </summary>
    /// <param name="distance"></param> target distance range
    /// <param name="shadMap"></param> shadowmap component
    /// <param name="mask"></param> shadowmask component

    public static void GetfrustumCorners(float2 nearFarPlane, float3* frustumCorners, float aspect, float relativeFov, float3 right, float3 up, float3 forward, float3 position)
    {
        //x: near width, y: near height z: far width w: far height
        float2 tanValue = tan(relativeFov) / float2(1, aspect);
        float4 screenDistanceNear = nearFarPlane.xxyy * tanValue.xyxy;
        float3 nearPlane = position + forward * nearFarPlane.x;
        float3 farPlane = position + forward * nearFarPlane.y;
        float3 upDir = screenDistanceNear.y * up;
        float3 rightDir = screenDistanceNear.x * right;
        frustumCorners[0] = nearPlane + rightDir + upDir;
        frustumCorners[1] = nearPlane - rightDir + upDir;
        frustumCorners[2] = nearPlane + rightDir - upDir;
        frustumCorners[3] = nearPlane - rightDir - upDir;
        upDir = screenDistanceNear.w * up;
        rightDir = screenDistanceNear.z * right;
        frustumCorners[4] = farPlane + rightDir + upDir;
        frustumCorners[5] = farPlane - rightDir + upDir;
        frustumCorners[6] = farPlane + rightDir - upDir;
        frustumCorners[7] = farPlane - rightDir - upDir;
    }


    public static void GetfrustumCorners(float* planes, int planesCount, Camera cam, float3* frustumCorners)
    {
        for (int i = 0; i < planesCount; ++i)
        {
            int index = i * 4;
            float p = planes[i];
            frustumCorners[index] = cam.ViewportToWorldPoint(new Vector3(0, 0, p));
            frustumCorners[1 + index] = cam.ViewportToWorldPoint(new Vector3(0, 1, p));
            frustumCorners[2 + index] = cam.ViewportToWorldPoint(new Vector3(1, 1, p));
            frustumCorners[3 + index] = cam.ViewportToWorldPoint(new Vector3(1, 0, p));
        }
    }


    public static bool FrustumCulling(ref Matrix4x4 ObjectToWorld, Vector3 extent, Vector4* frustumPlanes)
    {
        Vector3 right = new Vector3(ObjectToWorld.m00, ObjectToWorld.m10, ObjectToWorld.m20);
        Vector3 up = new Vector3(ObjectToWorld.m01, ObjectToWorld.m11, ObjectToWorld.m21);
        Vector3 forward = new Vector3(ObjectToWorld.m02, ObjectToWorld.m12, ObjectToWorld.m22);
        Vector3 position = new Vector3(ObjectToWorld.m03, ObjectToWorld.m13, ObjectToWorld.m23);
        for (int i = 0; i < 6; ++i)
        {
            ref Vector4 plane = ref frustumPlanes[i];
            Vector3 normal = new Vector3(plane.x, plane.y, plane.z);
            float distance = plane.w;
            float r = Vector3.Dot(position, normal);
            Vector3 absNormal = new Vector3(Mathf.Abs(Vector3.Dot(normal, right)), Mathf.Abs(Vector3.Dot(normal, up)), Mathf.Abs(Vector3.Dot(normal, forward)));
            float f = Vector3.Dot(absNormal, extent);
            if ((r - f) >= -distance)
                return false;
        }
        return true;
    }

    public static bool FrustumCulling(Vector3 position, float range, Vector4* frustumPlanes)
    {
        for (int i = 0; i < 5; ++i)
        {
            ref Vector4 plane = ref frustumPlanes[i];
            Vector3 normal = new Vector3(plane.x, plane.y, plane.z);
            float rayDist = Vector3.Dot(normal, position);
            rayDist += plane.w;
            if (rayDist > range)
            {
                return false;
            }
        }
        return true;
    }

    public static void SetShadowCameraPositionStaticFit(ref StaticFit fit, ref OrthoCam shadCam, int pass, float4x4* vpMatrices)
    {
        float range = 0;
        double3 averagePos = double3(0, 0, 0);
        int frustumStartPos = pass * 4;
        for (int i = 0; i < 8; ++i)
        {
            averagePos += fit.frustumCorners[i + frustumStartPos];
        }
        averagePos /= 8;
        for (int i = 0; i < 8; ++i)
        {
            double dist = distance(averagePos, fit.frustumCorners[i + frustumStartPos]);
            if (range < dist)
            {
                range = (float)dist;
            }
        }
        shadCam.size = range;
        float farClipPlane = fit.mainCamTrans.farClipPlane;
        float3 targetPosition = (float3)averagePos - shadCam.forward * farClipPlane * 0.5f;
        shadCam.nearClipPlane = 0;
        shadCam.farClipPlane = farClipPlane;
        ref float4x4 shadowVP = ref vpMatrices[pass];
        float4x4 invShadowVP = inverse(shadowVP);

        float4 ndcPos = mul(shadowVP, new float4(targetPosition, 1));
        ndcPos /= ndcPos.w;
        float2 uv = new float2(ndcPos.x, ndcPos.y) * 0.5f + new float2(0.5f, 0.5f);
        uv.x = (int)(uv.x * fit.resolution + 0.5);
        uv.y = (int)(uv.y * fit.resolution + 0.5);
        uv /= fit.resolution;
        uv = uv * 2f - 1;
        ndcPos = new float4(uv.x, uv.y, ndcPos.z, 1);
        float4 targetPos_4 = mul(invShadowVP, ndcPos);
        targetPosition = targetPos_4.xyz / targetPos_4.w;
        shadCam.position = targetPosition;
        shadCam.UpdateProjectionMatrix();
        shadCam.UpdateTRSMatrix();
        shadowVP = GL.GetGPUProjectionMatrix(shadCam.projectionMatrix, false) * (Matrix4x4)shadCam.worldToCameraMatrix;
    }
    /// <summary>
    /// Initialize per cascade shadowmap buffers
    /// </summary>
    public static void UpdateCascadeState(ref SunLight comp, CommandBuffer buffer, float bias, int pass, out Matrix4x4 rtVp)
    {
        float4 shadowcamDir = new float4(comp.shadCam.forward, bias);
        buffer.SetRenderTarget(comp.shadowmapTexture, 0, CubemapFace.Unknown, depthSlice: pass);
        buffer.ClearRenderTarget(true, true, Color.white);
        buffer.SetGlobalVector(ShaderIDs._ShadowCamDirection, shadowcamDir);
        rtVp = GL.GetGPUProjectionMatrix(comp.shadCam.projectionMatrix, true) * (Matrix4x4)comp.shadCam.worldToCameraMatrix;
        buffer.SetGlobalMatrix(ShaderIDs._ShadowMapVP, rtVp);
    }
    /// <summary>
    /// Initialize shadowmask per frame buffers
    /// </summary>


    public static void Dispose(PipelineBaseBuffer baseBuffer)
    {
        baseBuffer.verticesBuffer.Dispose();
        baseBuffer.clusterBuffer.Dispose();
        baseBuffer.instanceCountBuffer.Dispose();
        baseBuffer.resultBuffer.Dispose();
        baseBuffer.dispatchBuffer.Dispose();
        baseBuffer.reCheckCount.Dispose();
        if (baseBuffer.reCheckResult != null)
        {
            baseBuffer.reCheckResult.Dispose();
            baseBuffer.reCheckResult = null;
        }
    }
    /// <summary>
    /// Set Basement buffers
    /// </summary>
    public static void SetBaseBuffer(PipelineBaseBuffer baseBuffer, ComputeShader gpuFrustumShader, Vector4[] frustumCullingPlanes, CommandBuffer buffer)
    {
        var compute = gpuFrustumShader;
        buffer.SetComputeVectorArrayParam(compute, ShaderIDs.planes, frustumCullingPlanes);
        buffer.SetComputeBufferParam(compute, 0, ShaderIDs.clusterBuffer, baseBuffer.clusterBuffer);
        buffer.SetComputeBufferParam(compute, 0, ShaderIDs.instanceCountBuffer, baseBuffer.instanceCountBuffer);
        buffer.SetComputeBufferParam(compute, 1, ShaderIDs.instanceCountBuffer, baseBuffer.instanceCountBuffer);
        buffer.DispatchCompute(compute, 1, 1, 1, 1);
        buffer.SetComputeBufferParam(compute, 0, ShaderIDs.resultBuffer, baseBuffer.resultBuffer);
    }
    public static void SetBaseBuffer(PipelineBaseBuffer baseBuffer, ComputeShader gpuFrustumShader, float4* frustumCullingPlanes)
    {
        var compute = gpuFrustumShader;
        UnsafeUtility.MemCpy(backupFrustumArray.Ptr(), frustumCullingPlanes, sizeof(float4) * 6);
        compute.SetVectorArray(ShaderIDs.planes, backupFrustumArray);
        compute.SetBuffer(0, ShaderIDs.clusterBuffer, baseBuffer.clusterBuffer);
        compute.SetBuffer(0, ShaderIDs.instanceCountBuffer, baseBuffer.instanceCountBuffer);
        compute.SetBuffer(1, ShaderIDs.instanceCountBuffer, baseBuffer.instanceCountBuffer);
        compute.Dispatch(1, 1, 1, 1);
        compute.SetBuffer(0, ShaderIDs.resultBuffer, baseBuffer.resultBuffer);
    }
    private static Vector4[] backupFrustumArray = new Vector4[6];

    public static void SetBaseBuffer(PipelineBaseBuffer baseBuffer, ComputeShader gpuFrustumShader, float4* frustumCullingPlanes, CommandBuffer buffer)
    {
        var compute = gpuFrustumShader;
        UnsafeUtility.MemCpy(backupFrustumArray.Ptr(), frustumCullingPlanes, sizeof(float4) * 6);
        buffer.SetComputeVectorArrayParam(compute, ShaderIDs.planes, backupFrustumArray);
        buffer.SetComputeBufferParam(compute, 0, ShaderIDs.clusterBuffer, baseBuffer.clusterBuffer);
        buffer.SetComputeBufferParam(compute, 0, ShaderIDs.instanceCountBuffer, baseBuffer.instanceCountBuffer);
        buffer.SetComputeBufferParam(compute, 1, ShaderIDs.instanceCountBuffer, baseBuffer.instanceCountBuffer);
        buffer.DispatchCompute(compute, 1, 1, 1, 1);
        buffer.SetComputeBufferParam(compute, 0, ShaderIDs.resultBuffer, baseBuffer.resultBuffer);
    }

    public static void DrawLastFrameCullResult(
        PipelineBaseBuffer baseBuffer,
        CommandBuffer buffer, Material mat)
    {
        buffer.SetGlobalBuffer(ShaderIDs.resultBuffer, baseBuffer.resultBuffer);
        buffer.SetGlobalBuffer(ShaderIDs.verticesBuffer, baseBuffer.verticesBuffer);
        buffer.DrawProceduralIndirect(Matrix4x4.identity, mat, 0, MeshTopology.Triangles, baseBuffer.instanceCountBuffer, 0);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void DrawRecheckCullResult(
        PipelineBaseBuffer occBuffer,
        Material indirectMaterial, CommandBuffer buffer)
    {
        buffer.DrawProceduralIndirect(Matrix4x4.identity, indirectMaterial, 0, MeshTopology.Triangles, occBuffer.reCheckCount, 0);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RunCullDispatching(PipelineBaseBuffer baseBuffer, ComputeShader computeShader, bool isOrtho, CommandBuffer buffer)
    {
        buffer.SetComputeIntParam(computeShader, ShaderIDs._CullingPlaneCount, isOrtho ? 6 : 5);
        ComputeShaderUtility.Dispatch(computeShader, buffer, 0, baseBuffer.clusterCount, 64);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RunCullDispatching(PipelineBaseBuffer baseBuffer, ComputeShader computeShader, bool isOrtho)
    {
        computeShader.SetInt(ShaderIDs._CullingPlaneCount, isOrtho ? 6 : 5);
        ComputeShaderUtility.Dispatch(computeShader, 0, baseBuffer.clusterCount, 64);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RenderProceduralCommand(PipelineBaseBuffer buffer, Material material, CommandBuffer cb)
    {
        cb.SetGlobalBuffer(ShaderIDs.resultBuffer, buffer.resultBuffer);
        cb.SetGlobalBuffer(ShaderIDs.verticesBuffer, buffer.verticesBuffer);
        cb.DrawProceduralIndirect(Matrix4x4.identity, material, 0, MeshTopology.Triangles, buffer.instanceCountBuffer, 0);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void GetViewProjectMatrix(Camera currentCam, out Matrix4x4 vp, out Matrix4x4 invVP)
    {
        vp = GL.GetGPUProjectionMatrix(currentCam.projectionMatrix, false) * currentCam.worldToCameraMatrix;
        invVP = vp.inverse;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void InitRenderTarget(ref RenderTargets tar, Camera tarcam, CommandBuffer buffer)
    {
        buffer.GetTemporaryRT(tar.gbufferIndex[0], tarcam.pixelWidth, tarcam.pixelHeight, 0, FilterMode.Point, RenderTextureFormat.ARGB32);
        buffer.GetTemporaryRT(tar.gbufferIndex[1], tarcam.pixelWidth, tarcam.pixelHeight, 0, FilterMode.Point, RenderTextureFormat.ARGB32);
        buffer.GetTemporaryRT(tar.gbufferIndex[2], tarcam.pixelWidth, tarcam.pixelHeight, 0, FilterMode.Point, RenderTextureFormat.ARGB2101010);
        buffer.GetTemporaryRT(tar.gbufferIndex[3], tarcam.pixelWidth, tarcam.pixelHeight, 0, FilterMode.Bilinear, RenderTextureFormat.ARGBHalf);
        buffer.GetTemporaryRT(tar.gbufferIndex[4], tarcam.pixelWidth, tarcam.pixelHeight, 0, FilterMode.Point, RenderTextureFormat.RGHalf);
        buffer.GetTemporaryRT(tar.depthIdentifier, tarcam.pixelWidth, tarcam.pixelHeight, 32, FilterMode.Point, RenderTextureFormat.Depth);
        buffer.GetTemporaryRT(ShaderIDs._BackupMap, tarcam.pixelWidth, tarcam.pixelHeight, 0, FilterMode.Bilinear, RenderTextureFormat.ARGBHalf);
        tar.renderTargetIdentifier = tar.gbufferIndex[3];
        tar.backupIdentifier = ShaderIDs._BackupMap;
    }

    public static void ReleaseRenderTarget(CommandBuffer buffer, ref RenderTargets targets)
    {
        foreach (var i in targets.gbufferIndex)
        {
            buffer.ReleaseTemporaryRT(i);
        }
        buffer.ReleaseTemporaryRT(ShaderIDs._BackupMap);
        buffer.ReleaseTemporaryRT(targets.depthIdentifier);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ExecuteCommandBuffer(ref this PipelineCommandData data)
    {
        data.context.ExecuteCommandBuffer(data.buffer);
        data.buffer.Clear();
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ExecuteCommandBufferAsync(ref this PipelineCommandData data, CommandBuffer asyncBuffer, ComputeQueueType queueType)
    {
        data.context.ExecuteCommandBufferAsync(asyncBuffer, queueType);
        asyncBuffer.Clear();
    }

    public static void InsertTo<T>(this List<T> targetArray, T value, Func<T, T, int> compareResult)
    {
        Vector2Int range = new Vector2Int(0, targetArray.Count);
        while (true)
        {
            if (targetArray.Count == 0)
            {
                targetArray.Add(value);
                return;
            }
            else if (abs(range.x - range.y) == 1)
            {
                int compareX = compareResult(targetArray[range.x], value);
                if (compareX < 0)
                {
                    targetArray.Insert(range.x, value);
                    return;
                }
                else if (compareX > 0)
                {
                    if (range.y < targetArray.Count && compareResult(targetArray[range.y], value) == 0)
                    {
                        return;
                    }
                    else
                    {
                        targetArray.Insert(range.y, value);
                        return;
                    }
                }
                else
                {
                    return;
                }
            }
            else
            {
                int currentIndex = (int)((range.x + range.y) / 2f);
                int compare = compareResult(targetArray[currentIndex], value);
                if (compare == 0)
                {
                    return;
                }
                else
                {
                    if (compare < 0)
                    {
                        range.y = currentIndex;
                    }
                    else if (compare > 0)
                    {
                        range.x = currentIndex;
                    }
                }
            }
        }
    }
    public static void UpdateOcclusionBuffer(
        PipelineBaseBuffer basebuffer
        , ComputeShader coreShader
        , CommandBuffer buffer
        , HizOcclusionData occlusionData
        , Vector4[] frustumCullingPlanes
        , bool isOrtho)
    {
        buffer.SetComputeIntParam(coreShader, ShaderIDs._CullingPlaneCount, isOrtho ? 6 : 5);
        buffer.SetComputeVectorArrayParam(coreShader, ShaderIDs.planes, frustumCullingPlanes);
        buffer.SetComputeVectorParam(coreShader, ShaderIDs._CameraUpVector, occlusionData.lastFrameCameraUp);
        buffer.SetComputeBufferParam(coreShader, OcclusionBuffers.FrustumFilter, ShaderIDs.clusterBuffer, basebuffer.clusterBuffer);
        buffer.SetComputeTextureParam(coreShader, OcclusionBuffers.FrustumFilter, ShaderIDs._HizDepthTex, occlusionData.historyDepth);
        buffer.SetComputeBufferParam(coreShader, OcclusionBuffers.FrustumFilter, ShaderIDs.dispatchBuffer, basebuffer.dispatchBuffer);
        buffer.SetComputeBufferParam(coreShader, OcclusionBuffers.FrustumFilter, ShaderIDs.resultBuffer, basebuffer.resultBuffer);
        buffer.SetComputeBufferParam(coreShader, OcclusionBuffers.FrustumFilter, ShaderIDs.instanceCountBuffer, basebuffer.instanceCountBuffer);
        buffer.SetComputeBufferParam(coreShader, OcclusionBuffers.FrustumFilter, ShaderIDs.reCheckResult, basebuffer.reCheckResult);
        ComputeShaderUtility.Dispatch(coreShader, buffer, OcclusionBuffers.FrustumFilter, basebuffer.clusterCount, 64);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ClearOcclusionData(
        PipelineBaseBuffer baseBuffer, CommandBuffer buffer
        , ComputeShader coreShader)
    {
        buffer.SetComputeBufferParam(coreShader, OcclusionBuffers.ClearOcclusionData, ShaderIDs.dispatchBuffer, baseBuffer.dispatchBuffer);
        buffer.SetComputeBufferParam(coreShader, OcclusionBuffers.ClearOcclusionData, ShaderIDs.instanceCountBuffer, baseBuffer.instanceCountBuffer);
        buffer.SetComputeBufferParam(coreShader, OcclusionBuffers.ClearOcclusionData, ShaderIDs.reCheckCount, baseBuffer.reCheckCount);
        buffer.DispatchCompute(coreShader, OcclusionBuffers.ClearOcclusionData, 1, 1, 1);
    }
    public static void OcclusionRecheck(
        PipelineBaseBuffer baseBuffer
        , ComputeShader coreShader, CommandBuffer buffer
        , HizOcclusionData hizData)
    {
        buffer.SetComputeVectorParam(coreShader, ShaderIDs._CameraUpVector, hizData.lastFrameCameraUp);
        buffer.SetComputeBufferParam(coreShader, OcclusionBuffers.OcclusionRecheck, ShaderIDs.dispatchBuffer, baseBuffer.dispatchBuffer);
        buffer.SetComputeBufferParam(coreShader, OcclusionBuffers.OcclusionRecheck, ShaderIDs.reCheckResult, baseBuffer.reCheckResult);
        buffer.SetComputeBufferParam(coreShader, OcclusionBuffers.OcclusionRecheck, ShaderIDs.clusterBuffer, baseBuffer.clusterBuffer);
        buffer.SetComputeTextureParam(coreShader, OcclusionBuffers.OcclusionRecheck, ShaderIDs._HizDepthTex, hizData.historyDepth);
        buffer.SetComputeBufferParam(coreShader, OcclusionBuffers.OcclusionRecheck, ShaderIDs.reCheckCount, baseBuffer.reCheckCount);
        buffer.SetComputeBufferParam(coreShader, OcclusionBuffers.OcclusionRecheck, ShaderIDs.resultBuffer, baseBuffer.resultBuffer);
        buffer.DispatchCompute(coreShader, OcclusionBuffers.OcclusionRecheck, baseBuffer.dispatchBuffer, 0);
    }

    public static void CopyToCubeMap(RenderTexture cubemapArray, RenderTexture texArray, CommandBuffer buffer, int offset)
    {
        offset *= 6;
        for (int i = 0; i < 6; ++i)
        {
            buffer.CopyTexture(texArray, i, cubemapArray, offset + i);
        }
    }
}