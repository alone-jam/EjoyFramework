using EjoyFramework.Core.Unity;
using NUnit.Framework;
using UnityEngine;
namespace EjoyFramework.Tests
{
    public class DefaultEntityHelperTests
    {
        [TestCase(0)] [TestCase(17)] [TestCase(-17)] [TestCase(int.MaxValue)]
        public void ExplicitFrameworkId_IsPreservedWithoutUnityIdConversion(int id)
        {
            var go = new GameObject("Entity helper test");
            try
            {
                var entity = new DefaultEntityHelper(null).CreateEntity(go, null,
                    new EntityData { EntityId = id, AssetName = "Robot" });
                Assert.That(entity.Id, Is.EqualTo(id));
                Assert.That(entity.EntityAssetName, Is.EqualTo("Robot"));
                Assert.That(entity.Handle, Is.SameAs(go));
                Assert.That(go.GetComponent<EntityLogic>(), Is.Not.Null);
            }
            finally { Object.DestroyImmediate(go); }
        }
        [Test] public void MissingExplicitId_UsesVersionAppropriatePolicy()
        {
            var go = new GameObject("Entity ID policy test");
            try
            {
                var helper = new DefaultEntityHelper(null);
#if UNITY_6000_6_OR_NEWER
                var error = Assert.Throws<EjoyFramework.Core.FrameworkException>(() => helper.CreateEntity(go, null, null));
                StringAssert.Contains("EntityData.EntityId", error.Message);
                Assert.That(go.GetComponent<EntityLogic>(), Is.Null, "Rejected creation must not add logic.");
#else
                Assert.That(helper.CreateEntity(go, null, null).Id, Is.EqualTo(go.GetInstanceID()));
#endif
            }
            finally { Object.DestroyImmediate(go); }
        }
    }
}
