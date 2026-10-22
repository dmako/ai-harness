using Tomlyn.Model;
using Tomlyn.Serialization;

namespace Harness.Infrastructure.Configuration;

[TomlSerializable(typeof(TomlTable))]
internal sealed partial class ConfigurationTomlContext : TomlSerializerContext;
