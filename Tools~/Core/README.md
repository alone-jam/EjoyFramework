# Core lifecycle regressions

Run `dotnet test Tools~/Core/EjoyFramework.Core.Lifecycle.Tests.csproj --configuration Release`.

This project source-links the package's EntityManager tests and uses the existing full Core
support project. It runs the real entity manager and object pools with a fake resource manager
and entity helper on CoreCLR, including synchronous and asynchronous same-prefab isolation,
independent update/hide, idle instance reuse, and shutdown/release accounting.

This does not run Unity GameObjects or the default Unity entity helper. The shared entity tests,
`ResourceComponentModeTests`, and `StaticPhysicsHistoricalQueryTests` also belong to the Unity
EditMode suites. Those require the Unity test host and an installed, licensed Editor.
