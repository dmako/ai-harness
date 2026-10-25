# Trimming and Native AOT

All product assemblies enable `IsAotCompatible` so normal builds run trimming
and AOT analyzers with warnings treated as errors. `configuration/Release.pubxml`
enables `PublishTrimmed`, `PublishAot` and `SelfContained`. It disables the managed
single-file bundler: Native AOT produces native executables and SQLite ships as
a sidecar (`e_sqlite3.dll`, `libe_sqlite3.so` or `libe_sqlite3.dylib`). Keep it beside
the executables; managed bundle extraction is not part of this deployment.

## Reflection-free boundaries

- System.CommandLine binds CLI options and arguments through typed actions.
  Spectre.Console is used only for interactive terminal rendering.
- `HarnessJson` resolves generated assembly-local `ProjectJsonContext` metadata.
  Explicit `JsonObjectMap` objects preserve wire naming policies while ordinary
  dictionary keys remain data. Iterator-backed sequences are written directly.
  Add named DTOs to the owning context when extending a wire/storage model;
  unknown types fail rather than falling back to reflection.
- Configuration DTOs use setters so absent JSON properties retain initializers.
  Tomlyn uses the generated `ConfigurationTomlContext` for `TomlTable` values,
  including date/time types that pass through configuration editors.
- Backlog export writes YAML representation nodes, quotes string scalars and
  omits the document terminator so appended task sequences remain valid.
- Web, Collect and Share use the Web SDK and request delegate generator. HTTP
  output uses generated metadata or explicit JSON nodes/text. Assets, documents,
  migrations and provider packs remain embedded.

Use [release validation](releases.md) to check real artifacts in addition to
managed tests. The publisher checks repeated binary and native-library hashes.
The package verifier checks platform executable headers and the Native AOT
runtime marker, so small managed apphosts cannot pass as native executables.
Qualification is specific to the executed host, architecture and exact archive
hashes; preparing a CI job does not establish its execution result.
