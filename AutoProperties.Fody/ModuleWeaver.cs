using System.Collections.Generic;

using FodyTools;

namespace AutoProperties.Fody
{
    public sealed class ModuleWeaver : AbstractModuleWeaver
    {
        public override bool ShouldCleanReference => true;

        public override void Execute()
        {
            // System.Diagnostics.Debugger.Launch();

            var systemReferences = new SystemReferences(this);

            new PropertyAccessorWeaver(this, systemReferences).Execute();
            new BackingFieldAccessWeaver(ModuleDefinition, this).Execute();

            CleanReferences();
        }

        public override IEnumerable<string> GetAssembliesForScanning() => new[] { "mscorlib", "System", "System.Reflection", "System.Runtime", "netstandard", };

        private void CleanReferences() => new ReferenceCleaner(ModuleDefinition, this).RemoveAttributes();
    }
}
