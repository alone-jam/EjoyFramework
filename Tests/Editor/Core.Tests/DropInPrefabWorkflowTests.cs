#if UNITY_EDITOR
using EjoyFramework.Core.Coroutines;
using EjoyFramework.Core.Unity;
using EjoyFramework.Core.Unity.Resource;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
namespace EjoyFramework.Tests
{
    public sealed class DropInPrefabWorkflowTests
    {
        [SetUp] public void Setup()=>EjoyFramework.Core.Generated.FrameworkModuleRegistrations.RegisterAll();
        [TearDown] public void Cleanup()=>EjoyFramework.Core.Framework.Shutdown();
        public sealed class ProbeProcedure : EjoyFramework.Core.Procedure.ProcedureBase {}
        [Test] public void ProcedureInitializationStatusCoversStartupAndShutdown()
        {
            var manager=EjoyFramework.Core.Framework.GetModule<EjoyFramework.Core.Procedure.IProcedureManager>();
            Assert.IsFalse(manager.IsInitialized);
            manager.Initialize(EjoyFramework.Core.Framework.GetModule<EjoyFramework.Core.Fsm.IFsmManager>(),new ProbeProcedure());
            Assert.IsTrue(manager.IsInitialized);manager.StartProcedure<ProbeProcedure>();Assert.IsNotNull(manager.CurrentProcedure);
            ((EjoyFramework.Core.FrameworkModule)manager).Shutdown();Assert.IsFalse(manager.IsInitialized);
        }
        const string PrefabPath="Packages/com.ejoy.framework/Runtime/Prefabs/EjoyFramework.prefab";
        [TestCase("m_LogHelperTypeName",typeof(DefaultLogHelper))]
        [TestCase("m_JsonHelperTypeName",typeof(DefaultJsonHelper))]
        [TestCase("m_UIFormHelperTypeName",typeof(DefaultUIFormHelper))]
        public void ShippedPrefabUsesCurrentHelperTypes(string field,System.Type expected)
        {
            var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);Assert.NotNull(prefab);
            Component component=field=="m_UIFormHelperTypeName" ? (Component)prefab.GetComponentInChildren<UIComponent>(true) : prefab.GetComponent<BaseComponent>();
            Assert.AreEqual(expected.FullName,new SerializedObject(component).FindProperty(field).stringValue);
        }
        [Test] public void ShippedPrefabStandardCanvasesHaveUnitScale()
        {
            var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            var groups=prefab.GetComponentsInChildren<UIGroupCanvasHelper>(true);Assert.AreEqual(8,groups.Length);
            foreach(var group in groups)Assert.AreEqual(Vector3.one,group.transform.localScale,group.GroupName);
        }
        [Test] public void SimulationResourceLoadLeavesNativeMountToSceneHelper()
        {
            var coroutines=EjoyFramework.Core.Framework.GetModule<ICoroutineManager>();var loader=new EditorSimulationLoader(coroutines);
            loader.Initialize(null,null,null,null,()=>{},error=>Assert.Fail(error));
            bool ready=false;int scenes=SceneManager.sceneCount;
            Assert.DoesNotThrow(()=>loader.LoadSceneAsync("Assets/OnlySceneHelperMounts.unity",0,null,path=>ready=true,(path,error)=>Assert.Fail(error),null));
            Assert.IsTrue(ready,"Simulation has no bundles to prepare; native validation/mount belongs to DefaultSceneHelper");
            Assert.AreEqual(0,coroutines.RunningCount);Assert.AreEqual(scenes,SceneManager.sceneCount);
        }
        [Test] public void SimulationResourceUnloadLeavesNativeUnmountToSceneHelper()
        {
            var coroutines=EjoyFramework.Core.Framework.GetModule<ICoroutineManager>();var loader=new EditorSimulationLoader(coroutines);
            loader.Initialize(null,null,null,null,()=>{},error=>Assert.Fail(error));
            bool released=false;int scenes=SceneManager.sceneCount;
            Assert.DoesNotThrow(()=>loader.UnloadSceneAsync("Assets/OnlySceneHelperUnmounts.unity",path=>released=true,(path,error)=>Assert.Fail(error),null));
            Assert.IsTrue(released);Assert.AreEqual(0,coroutines.RunningCount);Assert.AreEqual(scenes,SceneManager.sceneCount);
        }
    }
}
#endif
