using System;
using System.Threading;
using EjoyFramework.Core.Ecs;
using NUnit.Framework;

namespace EjoyFramework.Tests.Ecs
{
    public class ReplicationSchemaTests
    {
        private struct State { public int Value; }
        private struct Missing { public int Value; }
        private struct PredictedState : IPredicted<PredictedState>
        {
            public int Value;
            public bool VerifyPrediction(in PredictedState predicted) => Value == predicted.Value;
        }
        private struct InterpolatedState : IInterpolated<InterpolatedState>
        {
            public float Value;
            public InterpolatedState Interpolate(in InterpolatedState to, float alpha)
                => new InterpolatedState { Value = Value + (to.Value - Value) * alpha };
        }

        [Test]
        public void Freeze_OrdersDescriptorsByExplicitSchemaId_NotTypeOrRegistrationOrder()
        {
            var schema = new ReplicationSchema();
            schema.RegisterPredicted<PredictedState>(30);
            schema.RegisterReplicated<State>(1);
            schema.RegisterInterpolated<InterpolatedState>(ushort.MaxValue);
            Assert.That(schema.IsFrozen, Is.False);
            schema.Freeze();
            Assert.That(schema.Count, Is.EqualTo(3));
            Assert.That(schema.GetAt(0).SchemaId, Is.EqualTo(1));
            Assert.That(schema.GetAt(1).SchemaId, Is.EqualTo(30));
            Assert.That(schema.GetAt(2).SchemaId, Is.EqualTo(ushort.MaxValue));
            Assert.That(schema.TryGet<PredictedState>(out var descriptor), Is.True);
            Assert.That(descriptor.Mode, Is.EqualTo(ReplicationMode.Predicted));
            Assert.That(descriptor.SchemaId, Is.EqualTo(30));
            Assert.That(schema.TryGet(ushort.MaxValue, out descriptor), Is.True);
            Assert.That(descriptor.Mode, Is.EqualTo(ReplicationMode.Interpolated));
        }

        [Test]
        public void DuplicateTypeIdOrMode_IsRejectedWithoutPartialRegistration()
        {
            var schema = new ReplicationSchema();
            Assert.Throws<ArgumentOutOfRangeException>(() => schema.RegisterReplicated<State>(0));
            schema.RegisterReplicated<State>(4);
            Assert.Throws<InvalidOperationException>(() => schema.RegisterReplicated<Missing>(4));
            Assert.Throws<InvalidOperationException>(() => schema.RegisterReplicated<State>(5));
            schema.RegisterPredicted<PredictedState>(6);
            Assert.Throws<InvalidOperationException>(() => schema.RegisterReplicated<PredictedState>(7));
            schema.RegisterReplicated<Missing>(5); // 被拒绝的条目没有占用类型或 ID。
            schema.Freeze();
            Assert.That(schema.Count, Is.EqualTo(3));
            Assert.That(schema.TryGet(7, out _), Is.False);
        }

        [Test]
        public void ReadsRequireFreeze_AndFreezePreventsFurtherMutation()
        {
            var schema = new ReplicationSchema();
            Assert.Throws<InvalidOperationException>(() => { int unused = schema.Count; });
            Assert.Throws<InvalidOperationException>(() => schema.TryGet<State>(out _));
            Assert.Throws<InvalidOperationException>(() => schema.TryGet(1, out _));
            Assert.Throws<InvalidOperationException>(() => schema.GetAt(0));
            schema.RegisterReplicated<State>(1);
            schema.Freeze();
            Assert.DoesNotThrow(() => schema.Freeze());
            Assert.Throws<InvalidOperationException>(() => schema.RegisterReplicated<Missing>(2));
            Assert.That(schema.Count, Is.EqualTo(1));
        }

        [Test]
        public void EmptyAndUnknownLookups_AreExplicit()
        {
            var empty = new ReplicationSchema();
            empty.Freeze();
            Assert.That(empty.Count, Is.Zero);
            Assert.That(empty.Descriptors.Length, Is.Zero);
            Assert.That(empty.TryGet<State>(out var descriptor), Is.False);
            Assert.That(descriptor.SchemaId, Is.Zero);
            Assert.That(empty.TryGet(0, out _), Is.False);
            Assert.Throws<ArgumentOutOfRangeException>(() => empty.GetAt(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => empty.GetAt(-1));
            Assert.That(empty.Count, Is.Zero);
        }

        [Test]
        public void Catalogs_AreIndependentAndProduceSameExternalOrder()
        {
            var first = new ReplicationSchema();
            first.RegisterPredicted<PredictedState>(9);
            first.RegisterReplicated<State>(2);
            first.Freeze();
            var second = new ReplicationSchema();
            second.RegisterReplicated<State>(2);
            second.RegisterPredicted<PredictedState>(9);
            second.Freeze();
            for (int i = 0; i < first.Count; i++)
            {
                Assert.That(first.GetAt(i).SchemaId, Is.EqualTo(second.GetAt(i).SchemaId));
                Assert.That(first.GetAt(i).Mode, Is.EqualTo(second.GetAt(i).Mode));
            }
            var independent = new ReplicationSchema();
            independent.RegisterReplicated<State>(100);
            independent.Freeze();
            Assert.That(independent.TryGet<State>(out var other), Is.True);
            Assert.That(first.TryGet<State>(out var original), Is.True);
            Assert.That(other.SchemaId, Is.EqualTo(100));
            Assert.That(original.SchemaId, Is.EqualTo(2));
        }

        [Test]
        public void SetupIsOwnerThreadOnly_FrozenReadsMayBeShared()
        {
            var schema = new ReplicationSchema();
            Exception registerError = null;
            Exception freezeError = null;
            var worker = new Thread(() =>
            {
                try { schema.RegisterReplicated<State>(1); } catch (Exception ex) { registerError = ex; }
                try { schema.Freeze(); } catch (Exception ex) { freezeError = ex; }
            });
            worker.IsBackground = true;
            worker.Start();
            Assert.That(worker.Join(10000), Is.True);
            Assert.That(registerError, Is.TypeOf<InvalidOperationException>());
            Assert.That(freezeError, Is.TypeOf<InvalidOperationException>());
            schema.RegisterReplicated<State>(1);
            schema.Freeze();
            bool found = false;
            Exception readError = null;
            worker = new Thread(() =>
            {
                try { found = schema.TryGet<State>(out var entry) && entry.SchemaId == 1 && schema.Count == 1; }
                catch (Exception ex) { readError = ex; }
            });
            worker.IsBackground = true;
            worker.Start();
            Assert.That(worker.Join(10000), Is.True);
            Assert.That(readError, Is.Null);
            Assert.That(found, Is.True);
        }

        [Test]
        public void WarmLookupAndReadOnlyEnumeration_AllocateNothing()
        {
            var schema = new ReplicationSchema();
            schema.RegisterReplicated<State>(1);
            schema.RegisterPredicted<PredictedState>(2);
            schema.RegisterInterpolated<InterpolatedState>(3);
            schema.Freeze();
            int found = 0;
            long sum = 0;
            TestDelegate body = () =>
            {
                found = 0;
                sum = 0;
                for (int i = 0; i < 10000; i++)
                {
                    if (schema.TryGet<State>(out var typed)) { found++; sum += typed.SchemaId; }
                    if (schema.TryGet(2, out var entry)) { found++; sum += entry.SchemaId; }
                    if (schema.TryGet<Missing>(out _)) found++;
                    foreach (var descriptor in schema.Descriptors) sum += descriptor.SchemaId;
                }
            };
            for (int i = 0; i < 8; i++) body();
            EcsAllocationTests.AssertZero(body);
            Assert.That(found, Is.EqualTo(20000));
            Assert.That(sum, Is.EqualTo(90000));
        }
    }
}
