# Gi.Validation

Pure numeric checks for Gi authoring tools. The assembly targets .NET 10 and .NET Standard 2.1 and contains no Unity dependency or runtime reference to Gi.Influence.

```csharp
using Gi.Validation;

if (!GiValidation.TryRectangle(width, height, value, out var error))
    ShowValidationError(error);
```

Successful checks return true and a null error. Failed checks return false and a deterministic error message; inputs are never clamped. Counts include zero and represent totals, rather than the next handle index. Stamp totals exclude the reserved zero handle.

`TryWorldIndex`, `TryLayerIndex`, and `TryStampIndex` validate zero-based register positions before converting them to bytes. Their ranges are 0–254, 0–31, and 0–254 respectively. These positions are authoring register indices, not runtime handles. A missing selection represented by -1 is rejected. Use count checks for register sizes; count 255 is valid for worlds and stamps, but index 255 is invalid. Checking that a selected asset belongs to a particular register remains the authoring tool's responsibility.

Use the netstandard2.1 Gi.Validation.dll in Unity with its Plugin Inspector restricted to Editor. Guard validation references and baking members in authoring assemblies with UNITY_EDITOR so MonoBehaviours remain available in player builds. Player assemblies must not reference Gi.Validation; it is tooling, not a game dependency. Membership in settings registers, Unity object references, and texture readability remain Unity authoring responsibilities.

Gi and Gi.Validation compile the same internal src/Gi/ValidationRules.cs source. The helper does not link to Gi.dll or require a new API on that runtime assembly, so it can validate inputs for an existing compatible Gi.Influence installation. Runtime exceptions and gain clamping remain unchanged. Editor grid validation additionally rejects nonfinite sizes; the runtime retains its legacy size check for compatibility.

Gi.Validation has an independent version, currently 0.1.0-alpha.2. Build and package it with dotnet pack src/Gi.Validation -c Release. Keep the helper version aligned with the runtime constraints it describes; do not replace published versions or tags.
