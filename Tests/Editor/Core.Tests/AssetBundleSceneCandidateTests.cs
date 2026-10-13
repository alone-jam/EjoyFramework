using System.Reflection;
using EjoyFramework.Core.Unity.Editor.Resource;
using NUnit.Framework;
using UnityEditor;
namespace EjoyFramework.Tests
{
    public class AssetBundleSceneCandidateTests
    {
        [Test] public void RuntimeSceneIsNotFilteredAsEditorOnlyAsset()
        {
            var method=typeof(AssetBundleBuilder).GetMethod("IsEditorOnlyAssetType",BindingFlags.NonPublic|BindingFlags.Static);
            Assert.That((bool)method.Invoke(null,new object[]{typeof(SceneAsset)}),Is.False);
            Assert.That((bool)method.Invoke(null,new object[]{typeof(MonoScript)}),Is.True);
            Assert.That((bool)method.Invoke(null,new object[]{typeof(UnityEngine.GameObject)}),Is.False);
        }
    }
}
