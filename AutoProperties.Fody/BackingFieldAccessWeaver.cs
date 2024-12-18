using System;
using System.Linq;

using FodyTools;

using Mono.Cecil;
using Mono.Cecil.Cil;

namespace AutoProperties.Fody
{
    internal sealed class BackingFieldAccessWeaver(ModuleDefinition moduleDefinition, ILogger logger)
    {
        private readonly ModuleDefinition _moduleDefinition = moduleDefinition;

        private readonly ISymbolReader? _symbolReader = moduleDefinition.SymbolReader;
        private readonly ILogger _logger = logger;

        internal void Execute()
        {
            var allTypes = _moduleDefinition.GetTypes();
            var allClasses = allTypes.Where(x => x != null && x.IsClass && (x.BaseType != null));

            try
            {
                foreach (var classDefinition in allClasses)
                {
                    var shouldBypassAutoPropertySetters = classDefinition.ShouldBypassAutoPropertySettersInConstructors()
                                                          ?? _moduleDefinition.Assembly.ShouldBypassAutoPropertySettersInConstructors()
                                                          ?? false;

                    var autoPropertyToBackingFieldMap = new AutoPropertyToBackingFieldMap(classDefinition);
                    var allMethods = classDefinition.Methods.Where(method => method.HasBody);

                    foreach (var method in allMethods)
                    {
                        if (method.IsConstructor && shouldBypassAutoPropertySetters)
                        {
                            BypassAutoPropertySetters(method, autoPropertyToBackingFieldMap);
                        }

                        ProcessExtensionMethodCalls(method, autoPropertyToBackingFieldMap);
                    }
                }
            }
            catch (Exception exception)
            {
                _logger.LogError($"""
                                  Unhandled exception. Weaving aborted.
                                  The most probable reason is that the module has no or incompatible debug information (.pdb).
                                  {exception}
                                  """);
            }
        }

        private void BypassAutoPropertySetters(MethodDefinition method, AutoPropertyToBackingFieldMap autoPropertyToBackingFieldMap)
        {
            var instructions = method.Body.Instructions;

            for (var index = 0; index < instructions.Count; index++)
            {
                var instruction = instructions[index];

                if (!instruction.IsPropertySetterCall(out var propertyName) || (propertyName is null))
                    continue;

                if (!autoPropertyToBackingFieldMap.TryGetValue(propertyName, out var propertyInfo))
                    continue;

                _logger.LogInfo($"Replace setter of property {propertyName} in method {method.FullName} with backing field assignment.");

                instructions[index] = Instruction.Create(OpCodes.Stfld, propertyInfo.BackingField);
            }
        }

        private void ProcessExtensionMethodCalls(MethodDefinition method, AutoPropertyToBackingFieldMap autoPropertyToBackingFieldMap)
        {
            var processor = new ExtensionMethodProcessor(_logger, _symbolReader, method, autoPropertyToBackingFieldMap);

            processor.ProcessExtensionMethodCalls("SetBackingField", static propertyInfo => Instruction.Create(OpCodes.Stfld, propertyInfo.BackingField));
            processor.ProcessExtensionMethodCalls("SetProperty", static propertyInfo => Instruction.Create(OpCodes.Call, propertyInfo.Property.SetMethod));
        }

        private sealed class ExtensionMethodProcessor(ILogger logger, ISymbolReader? symbolReader, MethodDefinition method, AutoPropertyToBackingFieldMap autoPropertyToBackingFieldMap)
        {
            private readonly ILogger _logger = logger;
            private readonly MethodDefinition _method = method;
            private readonly AutoPropertyToBackingFieldMap _autoPropertyToBackingFieldMap = autoPropertyToBackingFieldMap;
            private readonly InstructionSequences _instructionSequences = new(method.Body.Instructions, method.ReadSequencePoints(symbolReader));

            public void ProcessExtensionMethodCalls(string extensionMethodName, Func<AutoPropertyInfo, Instruction> createInstruction)
            {
                foreach (var sequence in _instructionSequences)
                {
                    if (!ProcessSequence(sequence, extensionMethodName, createInstruction))
                        return;
                }
            }

            private bool ProcessSequence(InstructionSequence sequence, string extensionMethodName, Func<AutoPropertyInfo, Instruction> createInstruction)
            {
                for (var index = 0; index < sequence.Count; index++)
                {
                    var instruction = sequence[index];

                    if (!instruction.IsExtensionMethodCall(extensionMethodName))
                        continue;

                    if (sequence.Count < 4
                        || sequence[0].OpCode != OpCodes.Ldarg_0
                        || !sequence[1].IsPropertyGetterCall(out var propertyName)
                        //|| sequence.Skip(index + 1).Any(inst => inst?.OpCode != OpCodes.Nop)
                        || sequence.Skip(index + 1).Any(inst => inst is null || inst.OpCode.Code is not Code.Nop and not Code.Ret)
                        || !_autoPropertyToBackingFieldMap.TryGetValue(propertyName, out var propertyInfo))
                    {
                        var message = $"Invalid usage of extension method '{extensionMethodName}()': '{extensionMethodName}()' is only valid on auto-properties of class {_method.DeclaringType?.Name}";

                        _logger.LogError(message, sequence.Point);

                        return false;
                    }

                    _logger.LogInfo($"Replace {extensionMethodName}() on property {propertyName} in method {_method}.");

                    sequence[index] = createInstruction(propertyInfo!);
                    sequence.RemoveAt(1);

                    return true;
                }

                return true;
            }
        }
    }
}
