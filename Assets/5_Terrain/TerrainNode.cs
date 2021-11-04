using System.Collections;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// CPU维护四叉树
/// TODO：放在CPU端，四叉树更新的开销相对还是比较高的，需要实现GPU QuadTree
/// </summary>
public class TerrainNode
{
    public float4 rect;
    public int mip;
    public int index;
    public NodeInfo info;

    public TerrainNode[] children;
    public TerrainNode(float4 r)
    {
        rect = r;
        mip = -1;
        index = -1;
    }

    public TerrainNode(float4 r, int m)
    {
        rect = r;
        mip = m;
        index = -1;
        info = new NodeInfo(new float4(r.x, r.y, r.z, r.w), m);
        if(mip > 0)
        {
            children = new TerrainNode[4];
            var halfX = r.z / 2;
            var halfY = r.w / 2;
            int subMip = m - 1;
            children[0] = new TerrainNode(new float4(r.x, r.y, halfX, halfY), subMip);
            children[1] = new TerrainNode(new float4(r.x + halfX, r.y, halfX, halfY), subMip);
            children[2] = new TerrainNode(new float4(r.x, r.y + halfY, halfX, halfY), subMip);
            children[3] = new TerrainNode(new float4(r.x + halfX, r.y + halfY, halfX, halfY), subMip);
        }
    }

    public TerrainNode GetActiveNode(Vector2 center)
    {
        if(Contains(center))
        {
            if (index >= 0)
                return this;
            else
            {
                foreach (var child in children)
                {
                    var active = child.GetActiveNode(center);
                    if (active != null)
                        return active;
                }
            }
        }
        return null;
    }

    bool Contains(Vector2 center)
    {
        return (center.x - rect.x) * (center.x - rect.x - rect.z) <= 0 && (center.y - rect.y) * (center.y - rect.y - rect.w) <= 0;
    }

    public void CollectNodeInfo(Vector2 center, List<NodeInfo> allNodeInfo, float[] lods)
    {
        // 非Root，剩下mip=0全部，mip>0的lodDistance外的，水平Distance作为LODDistance
        if (mip >= 0 && (mip == 0 || (center - new Vector2(rect.x + rect.z * 0.5f, rect.y + rect.w * 0.5f)).magnitude >= lods[mip]))
        {
            index = allNodeInfo.Count;
            allNodeInfo.Add(this.info);
        }
        else
        {
            index = -1;
            foreach (var child in children)
            {
                child.CollectNodeInfo(center, allNodeInfo, lods);
            }
        }
    }

}


public struct NodeInfo
{
    public float4 rect;
    public int mip;
    public int neighbor;

    public NodeInfo(float4 r, int m)
    {
        rect = r;
        mip = m;
        neighbor = 0;
    }
}