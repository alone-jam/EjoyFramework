using System.Reflection;
using EjoyFramework.Core;
using EjoyFramework.Core.Resource;
using EjoyFramework.Core.Unity;
using NUnit.Framework;
using UnityEngine;

namespace EjoyFramework.Tests
{
    public sealed class ResourceComponentModeTests
    {
        private GameObject host;
        private ResourceComponent component;
        private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

        [SetUp]
        public void Setup()
        {
            host = new GameObject("Unsupported resource mode test");
            host.SetActive(false); // Configure the serialized mode before Awake runs.
            component = host.AddComponent<ResourceComponent>();
            typeof(ResourceComponent).GetField("m_ResourceMode", PrivateInstance)
                .SetValue(component, ResourceMode.Resources);
        }

        [TearDown]
        public void Cleanup() => Object.DestroyImmediate(host);

        [TestCase("Awake")]
        [TestCase("CreateLoader")]
        public void ResourcesMode_RejectsBeforeInstallingOrSelectingAnyLoader(string methodName)
        {
            var method = typeof(ResourceComponent).GetMethod(methodName, PrivateInstance);
            var args = methodName == "CreateLoader" ? new object[] { ResourceMode.Resources } : null;
            var error = Assert.Throws<TargetInvocationException>(() => method.Invoke(component, args));
            Assert.That(error.InnerException, Is.TypeOf<FrameworkException>());
            StringAssert.Contains("ResourceMode.Resources is not implemented", error.InnerException.Message);
            Assert.That(typeof(ResourceComponent).GetField("m_Loader", PrivateInstance).GetValue(component), Is.Null);
            Assert.That(typeof(ResourceComponent).GetField("m_ResourceManager", PrivateInstance).GetValue(component), Is.Null);
        }

        [Test]
        public void ResourcesMode_InitializeAsyncReportsFailureWithoutStartingInitialization()
        {
            bool completed = false;
            string failure = null;
            component.InitializeAsync(() => completed = true, error => failure = error);
            Assert.That(completed, Is.False);
            StringAssert.Contains("ResourceMode.Resources is not implemented", failure);
            StringAssert.Contains("AssetBundle", failure);
            Assert.That(component.IsInitialized, Is.False);
            Assert.That(typeof(ResourceComponent).GetField("m_InitTriggered", PrivateInstance).GetValue(component), Is.False);
        }
    }
}
