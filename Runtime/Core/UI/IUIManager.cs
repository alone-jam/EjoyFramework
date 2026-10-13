//------------------------------------------------------------
// EjoyGame Framework
// Copyright (c) 2024-2026 EjoyGame. All rights reserved.
//------------------------------------------------------------

using System;

namespace EjoyFramework.Core.UI
{
    /// <summary>
    /// 界面管理器接口。
    /// </summary>
    public interface IUIManager
    {
        /// <summary>
        /// 获取界面组数量。
        /// </summary>
        int UIGroupCount { get; }

        /// <summary>
        /// 打开界面成功事件。
        /// </summary>
        event EventHandler<OpenUIFormSuccessEventArgs> OpenUIFormSuccess;

        /// <summary>
        /// 打开界面失败事件。
        /// </summary>
        event EventHandler<OpenUIFormFailureEventArgs> OpenUIFormFailure;

        /// <summary>
        /// 关闭界面完成事件。
        /// </summary>
        event EventHandler<CloseUIFormCompleteEventArgs> CloseUIFormComplete;

        /// <summary>
        /// 是否存在界面组。
        /// </summary>
        bool HasUIGroup(string uiGroupName);

        /// <summary>
        /// 获取界面组。
        /// </summary>
        IUIGroup GetUIGroup(string uiGroupName);

        /// <summary>
        /// 获取所有界面组。
        /// </summary>
        IUIGroup[] GetAllUIGroups();

        /// <summary>
        /// 增加界面组。
        /// </summary>
        bool AddUIGroup(string uiGroupName, int uiGroupDepth, IUIGroupHelper uiGroupHelper);

        /// <summary>
        /// 是否存在界面。
        /// </summary>
        bool HasUIForm(int serialId);

        /// <summary>
        /// 是否存在界面。
        /// </summary>
        bool HasUIForm(string uiFormAssetName);

        /// <summary>
        /// 获取界面。
        /// </summary>
        IUIForm GetUIForm(int serialId);

        /// <summary>
        /// 获取界面。
        /// </summary>
        IUIForm GetUIForm(string uiFormAssetName);

        /// <summary>
        /// 获取所有已加载的界面。
        /// </summary>
        IUIForm[] GetAllLoadedUIForms();

        /// <summary>
        /// 打开界面。
        /// </summary>
        int OpenUIForm(string uiFormAssetName, string uiGroupName, int priority, bool pauseCoveredUIForm, object userData);

        /// <summary>
        /// 预读下次 OpenUIForm 将分配的 SerialId（不实际分配）。
        /// 用于业务方在 OpenUIForm 同步路径（实例池命中 / 同步资源加载）触发 OpenUIFormSuccess 事件之前预注册按 serial 路由的回调。
        /// 注意：必须在同一线程内 PeekNextSerial → OpenUIForm 相邻调用；不可跨线程或交错调用其他 OpenUIForm。
        /// </summary>
        int PeekNextOpenUIFormSerial();

        /// <summary>
        /// 关闭界面。
        /// </summary>
        void CloseUIForm(int serialId);

        /// <summary>
        /// 关闭界面。
        /// </summary>
        void CloseUIForm(int serialId, object userData);

        /// <summary>
        /// 关闭所有已加载的界面。
        /// </summary>
        void CloseAllLoadedUIForms();

        /// <summary>
        /// 关闭所有正在加载的界面。
        /// </summary>
        void CloseAllLoadingUIForms();

        /// <summary>
        /// 激活界面。
        /// </summary>
        void RefocusUIForm(IUIForm uiForm);

        /// <summary>
        /// 设置界面辅助器。
        /// </summary>
        void SetUIFormHelper(IUIFormHelper uiFormHelper);

        /// <summary>Release idle pooled forms and their asset references. Active forms are retained.</summary>
        void ReleaseUnusedUIForms();
    }

    /// <summary>
    /// 界面接口。
    /// </summary>
    public interface IUIForm
    {
        int SerialId { get; }
        string UIFormAssetName { get; }
        object Handle { get; }
        IUIGroup UIGroup { get; }
        int DepthInUIGroup { get; }
        bool PauseCoveredUIForm { get; }

        void OnInit(int serialId, string uiFormAssetName, IUIGroup uiGroup, bool pauseCoveredUIForm, bool isNewInstance, object userData);
        void OnRecycle();
        void OnOpen(object userData);
        void OnClose(bool isShutdown, object userData);
        void OnPause();
        void OnResume();
        void OnCover();
        void OnReveal();
        void OnRefocus(object userData);
        void OnUpdate(float elapseSeconds, float realElapseSeconds);
        void OnDepthChanged(int uiGroupDepth, int depthInUIGroup);
    }

    /// <summary>
    /// 界面组接口。
    /// </summary>
    public interface IUIGroup
    {
        string Name { get; }
        int Depth { get; set; }
        bool Pause { get; set; }
        int UIFormCount { get; }
        IUIForm CurrentUIForm { get; }
    }

    /// <summary>
    /// 界面辅助器接口。
    /// </summary>
    public interface IUIFormHelper
    {
        object InstantiateUIForm(object uiFormAsset);
        IUIForm CreateUIForm(object uiFormInstance, IUIGroup uiGroup, object userData);
        void ReleaseUIForm(object uiFormAsset, object uiFormInstance);
    }

    /// <summary>
    /// 界面组辅助器接口（Strategy of "where forms in this group live"）。
    ///
    /// 实现方负责 form 进入 group 时的容器化操作。Unity 层 <c>UIGroupCanvasHelper</c>
    /// 在此实现"把 form GameObject 设为 Canvas 子节点"。其他后端可用本接口接入到不同的 UI 树。
    ///
    /// <para>设计约束：framework 不允许在 UIComponent 层按 GameObject 名字反查 helper —
    /// 那种命名约定耦合（"Canvas:&lt;name&gt;" vs "UIGroup_&lt;name&gt;"）是脆弱设计。
    /// helper 引用由 <see cref="IUIManager.AddUIGroup"/> 注册时直接持有，
    /// 后续路径全部通过此引用调用，无字符串查找。</para>
    /// </summary>
    public interface IUIGroupHelper
    {
        /// <summary>
        /// Framework 在 form 进入本 group 时调用一次。
        /// <see cref="IUIForm.Handle"/> 由具体 helper 解读（Unity 层即 GameObject）。
        /// </summary>
        void AttachUIForm(IUIForm form);
    }

    /// <summary>
    /// 打开界面成功事件。
    /// </summary>
    public sealed class OpenUIFormSuccessEventArgs : FrameworkEventArgs
    {
        public IUIForm UIForm { get; private set; }
        public float Duration { get; private set; }
        public object UserData { get; private set; }

        public override void Clear()
        {
            UIForm = null;
            Duration = 0f;
            UserData = null;
        }

        public static OpenUIFormSuccessEventArgs Create(IUIForm uiForm, float duration, object userData)
        {
            var e = ReferencePool.Acquire<OpenUIFormSuccessEventArgs>();
            e.UIForm = uiForm;
            e.Duration = duration;
            e.UserData = userData;
            return e;
        }
    }

    /// <summary>
    /// 打开界面失败事件。
    /// </summary>
    public sealed class OpenUIFormFailureEventArgs : FrameworkEventArgs
    {
        public int SerialId { get; private set; }
        public string UIFormAssetName { get; private set; }
        public string UIGroupName { get; private set; }
        public string ErrorMessage { get; private set; }
        public object UserData { get; private set; }

        public override void Clear()
        {
            SerialId = 0;
            UIFormAssetName = null;
            UIGroupName = null;
            ErrorMessage = null;
            UserData = null;
        }

        public static OpenUIFormFailureEventArgs Create(int serialId, string assetName, string groupName, string errorMessage, object userData)
        {
            var e = ReferencePool.Acquire<OpenUIFormFailureEventArgs>();
            e.SerialId = serialId;
            e.UIFormAssetName = assetName;
            e.UIGroupName = groupName;
            e.ErrorMessage = errorMessage;
            e.UserData = userData;
            return e;
        }
    }

    /// <summary>
    /// 关闭界面完成事件。
    /// </summary>
    public sealed class CloseUIFormCompleteEventArgs : FrameworkEventArgs
    {
        public int SerialId { get; private set; }
        public string UIFormAssetName { get; private set; }
        public IUIGroup UIGroup { get; private set; }
        public object UserData { get; private set; }

        public override void Clear()
        {
            SerialId = 0;
            UIFormAssetName = null;
            UIGroup = null;
            UserData = null;
        }

        public static CloseUIFormCompleteEventArgs Create(int serialId, string assetName, IUIGroup uiGroup, object userData)
        {
            var e = ReferencePool.Acquire<CloseUIFormCompleteEventArgs>();
            e.SerialId = serialId;
            e.UIFormAssetName = assetName;
            e.UIGroup = uiGroup;
            e.UserData = userData;
            return e;
        }
    }
}
