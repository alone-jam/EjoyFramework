//------------------------------------------------------------
// EjoyGame Framework
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

using System;
using System.Collections.Generic;

namespace EjoyFramework.Core.ObjectPool
{
    /// <summary>
    /// 对象池管理器接口。
    /// </summary>
    public interface IObjectPoolManager
    {
        /// <summary>
        /// 获取对象池数量。
        /// </summary>
        int Count { get; }

        /// <summary>
        /// 检查是否存在对象池。
        /// </summary>
        bool HasObjectPool<T>() where T : ObjectBase;

        /// <summary>Checks for the pool identified by its object type and name.</summary>
        bool HasObjectPool<T>(string name) where T : ObjectBase;

        /// <summary>
        /// 获取对象池。
        /// </summary>
        IObjectPool<T> GetObjectPool<T>() where T : ObjectBase;

        /// <summary>Access the pool identified by its object type and name.</summary>
        IObjectPool<T> GetObjectPool<T>(string name) where T : ObjectBase;

        /// <summary>
        /// 创建允许单次获取的对象池。
        /// </summary>
        IObjectPool<T> CreateSingleSpawnObjectPool<T>(string name, float autoReleaseInterval, int capacity, float expireTime, int priority) where T : ObjectBase;

        /// <summary>
        /// 创建允许多次获取的对象池。
        /// </summary>
        IObjectPool<T> CreateMultiSpawnObjectPool<T>(string name, float autoReleaseInterval, int capacity, float expireTime, int priority) where T : ObjectBase;

        /// <summary>
        /// 销毁对象池。
        /// </summary>
        bool DestroyObjectPool<T>() where T : ObjectBase;

        /// <summary>Destroys the pool identified by its object type and name.</summary>
        bool DestroyObjectPool<T>(string name) where T : ObjectBase;

        /// <summary>
        /// 释放对象池中的可释放对象。
        /// </summary>
        void Release();

        /// <summary>
        /// 释放对象池中的所有未使用对象。
        /// </summary>
        void ReleaseAllUnused();

        /// <summary>
        /// 获取所有对象池基类引用（调试/统计用，请勿修改）。
        /// </summary>
        ObjectPoolBase[] GetAllObjectPools();

        /// <summary>获取所有对象池（非分配版本：写入调用方提供的列表，列表先被清空）。</summary>
        void GetAllObjectPools(List<ObjectPoolBase> results);
    }
}
