# AGENTS.md — Unity Agents Rules & Enforcement

> **Scope:** This document governs how _agents_ (AI‑driven entities) are declared, loaded, and executed in this Unity project. It is intentionally strict. If something violates a rule, the correct response is to **fail fast** and fix the data or code — not to add a fallback.

---

## Compatibility Targets (Must Match Project Settings)

- **Unity:** 6.x
- **API Compatibility Level:** **.NET Standard 2.1**
- **Language use:** Prefer C# 8 features (for broad tooling compatibility). Avoid language constructs that require newer runtimes than .NET Standard 2.1.
- **Assemblies:** Use Unity **Assembly Definition** files (`.asmdef`) for modularity, but **do not** add new external projects or solutions.

> Project Settings → Player → Other Settings → **Api Compatibility Level: .NET Standard 2.1**

---

## Non‑Negotiable Rules (Culturally Enforced & Build‑Reviewed)

1) **Never create fail‑safe or fallback routines.**
   - If data is missing, malformed, or incompatible: **throw** and stop the run.
   - Do not catch exceptions to continue; only catch to **add context** and rethrow.
   - Forbidden examples:
     - Swallowing exceptions
     - Returning a “default” object when load fails
     - Auto‑creating configs on missing files
     - Silently substituting placeholder assets or actions

2) **Always load simulation objects from the DataDrivenGoap simulation.**
   - Agents, actions, goals, stations, items, world state, time configuration, and activities must be **declared in DataDrivenGoap data** and loaded at runtime.
   - Hard‑coding these in C# is **not allowed** (beyond minimal glue/bootstrap).
   - Runtime should **fail immediately** if an agent or action is referenced that is not present/valid in DataDrivenGoap data.

3) **Do not create additional projects.**
   - No new `.csproj`, no new plugin solutions, no external analyzer projects.
   - Keep everything inside this Unity project with asmdefs as needed.
   - If new capabilities are needed, they must be implemented within the existing Unity solution or as data/config in DataDrivenGoap.

4) **Do not use preprocessor commands or directives.**
   - No `#if`, `#define`, `#pragma`, `#warning`, or conditional compilation for feature flags.
   - All variability must be **data‑driven** or handled at runtime (not compile‑time).

5) **Target Unity 6 and .NET Standard 2.1.**
   - Avoid APIs not present in .NET Standard 2.1.
   - If a third‑party package requires a higher API profile, it is **not permitted**.

6) **Absolute prohibition on test code and test projects.**
   - **No test projects** of any kind (no new `.csproj`/solutions, no “Tests” asmdefs).
   - **No test classes**. In particular: **when a class `N` is created, a matching `NTests` (or `NTest`) **must not** be created**.
   - **No test frameworks** referenced or used: **NUnit**, **Unity Test Framework** (`UnityEngine.TestTools`), **xUnit**, **MSTest**, etc.
   - **No attributes** such as `[Test]`, `[UnityTest]`, `[TestCase]`, `[SetUp]`, `[TearDown]`.
   - **No namespaces or folders** named `Tests`, `Test`, `EditorTests`, `PlayModeTests` (or similar).
   - **No asmdefs** whose name contains `Test` or `Tests`, and no asmdef references to `UnityEditor.TestRunner` or `UnityEngine.TestRunner`.
   - **PRs that introduce any of the above are rejected** on sight.

---

## Architecture at a Glance

```
+--------------------+
|   Scene Bootstrap  |  MonoBehaviour that kicks off loading
+---------+----------+
          |
          v
+--------------------+
|  DataDrivenGoap    |  Single Source of Truth (SSOT):
|  Data Loader       |  agents, goals, actions, items, time, stations
+---------+----------+
          |
          v
+--------------------+
|   Agent Host       |  Wires loaded definitions to live agents,
|   (Runtime)        |  schedules updates/ticks, logs deterministically
+---------+----------+
          |
          v
+--------------------+
|  Simulation Loop   |  Runs with strict invariants. Any missing or bad
|  (Unity Update)    |  data => throw and stop.
+--------------------+
```

**Key Principle:** _C# is glue; data is the law._ If behavior isn’t declared in DataDrivenGoap, it does not exist.

---

## Minimal Glue Pattern (No Fallbacks, No Preprocessor)

> Illustrative snippet — adapt names to your project. Note: this is **not** a fallback; it fails fast on any issue.

```csharp
using System;
using System.IO;
using UnityEngine;

public sealed class SimulationBootstrapper : MonoBehaviour
{
    [SerializeField] private TextAsset goapDatasetJson; // Or file path from settings

    private ISimulationHost _host;

    private void Awake()
    {
        if (goapDatasetJson == null || string.IsNullOrWhiteSpace(goapDatasetJson.text))
            throw new InvalidDataException("GOAP dataset is missing. Loading cannot proceed.");

        // Load dataset strictly from DataDrivenGoap (no default objects)
        var dataset = DataDrivenGoap.Adapter.Parse(goapDatasetJson.text); // must throw on invalid/missing parts
        _host = new SimulationHost();
        _host.Initialize(dataset); // must throw if required defs are missing
    }

    private void Update()
    {
        _host?.Tick(Time.deltaTime); // host must assume data is valid; errors should throw
    }
}
```

**Forbidden variants of the above:**
- `try { ... } catch { /* continue */ }`
- `dataset ??= CreateDefaultDataset();`
- Conditional compilation like `#if UNITY_EDITOR` guarded code paths.

---

## Anti‑Patterns (with Corrections)

- **Anti‑pattern:**  
  ```csharp
  // if agent type isn't found, spawn a dummy agent so the game doesn't crash
  if (!defs.TryGetAgent(type, out var def)) def = AgentDef.CreateDefault();
  ```
  **Correct:**  
  ```csharp
  if (!defs.TryGetAgent(type, out var def))
      throw new InvalidDataException($"Agent '{type}' not found in DataDrivenGoap dataset.");
  ```

- **Anti‑pattern:**  
  ```csharp
  // on missing time config, assume 24h day
  if (!world.HasTimeConfig) world.SetDefaultTime();
  ```
  **Correct:**  
  ```csharp
  if (!world.HasTimeConfig)
      throw new InvalidDataException("A time configuration is required in the dataset.");
  ```

- **Anti‑pattern:**  
  ```csharp
  // quick fix for build
  #if UNITY_EDITOR
  LoadEditorOnlyShims();
  #endif
  ```
  **Correct:** Remove. Variations must be data/runtime driven without preprocessor guards.

---

## Enforcement: Review & Runtime Contracts (No Tests)

### Pull Request Checklist (copy into PR template)
- [ ] **No test code** added: no classes named `*Test`/`*Tests`, no `[Test]`/`[UnityTest]` attributes, no NUnit/xUnit/MSTest references.
- [ ] **No test assemblies** or asmdefs named with `Test`/`Tests`; no `Unity Test Framework` in asmdef references.
- [ ] No new projects (.csproj/solutions) added.
- [ ] No preprocessor directives (`#if`, `#define`, etc.).
- [ ] All simulation objects (agents, actions, goals, items, time, stations) are defined in **DataDrivenGoap** data.
- [ ] No fail‑safes/fallbacks: missing/malformed data **throws**. No dummy defaults.
- [ ] API usage compatible with **.NET Standard 2.1**.
- [ ] Bootstrapping is minimal and does not create behavior outside the dataset.

### Runtime Guardrails
- Loader APIs must **throw** on missing or malformed sections. Do not return partial objects.
- The Agent Host must assume **valid** inputs; any inconsistency should throw immediately with a clear message.
- All exceptions must **preserve context** (wrap with additional data, then rethrow). No suppression.
- No testing hooks, no conditional paths for “test builds,” and no code that exists solely to support tests.

---

## “Review Dialogue” (Reality Check)

**Reviewer:** “Where does this agent’s cooking behavior come from?”  
**Developer:** “From the DataDrivenGoap dataset `agents.json` and `actions.json`; the bootstrapper only loads and wires. No C# behavior defaults.”

**Reviewer:** “What happens if the dataset’s time section is missing?”  
**Developer:** “Load throws `InvalidDataException("A time configuration is required")`. There is no fallback.”

**Reviewer:** “I don’t see any `#if` guards for editor vs player?”  
**Developer:** “Correct. Variability is in data. The same loader runs everywhere; if data’s invalid, it fails the same way.”

**Reviewer:** “Where are the tests for class `N`?”  
**Developer:** “Per project policy, **no tests** are created. In particular, **no `NTests` is ever created for class `N`.**”

---

## Summary

- **No fallbacks.**  
- **All simulation objects from DataDrivenGoap.**  
- **No new projects.**  
- **No preprocessor directives.**  
- **Unity 6 + .NET Standard 2.1 only.**  
- **No tests — ever (no projects, no classes, no frameworks).**
