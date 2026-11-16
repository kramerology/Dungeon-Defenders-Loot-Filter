global using System.IO;
// A hero's equipped gear: EquipmentType -> stat id -> value. Null where the hero can't be read.
global using Gear = System.Collections.Generic.IReadOnlyDictionary<int, System.Collections.Generic.IReadOnlyDictionary<string, int>>;
