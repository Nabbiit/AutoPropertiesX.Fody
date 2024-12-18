using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using FodyTools;

using Mono.Cecil;
using Mono.Cecil.Cil;

namespace AutoProperties.Fody
{
    internal static class ExtensionMethods
    {
        public static bool? ShouldBypassAutoPropertySettersInConstructors(this ICustomAttributeProvider? node) =>
            node?.CustomAttributes.GetAttribute(AttributeNames.BypassAutoPropertySettersInConstructors)?.ConstructorArguments?.Select(arg => arg.Value as bool?).FirstOrDefault();

        public static CustomAttribute? GetAttribute(this IEnumerable<CustomAttribute> attributes, string? attributeName) => attributes.FirstOrDefault(attribute => attribute.Constructor?.DeclaringType?.FullName == attributeName);

        public static bool IsPropertySetterCall(this Instruction instruction, [NotNullWhen(true)] out string? propertyName) => IsPropertyCall(instruction, "set_", out propertyName);

        public static bool IsPropertyGetterCall(this Instruction instruction, [NotNullWhen(true)] out string? propertyName) => IsPropertyCall(instruction, "get_", out propertyName);

        private static bool IsPropertyCall(this Instruction instruction, string prefix, [NotNullWhen(true)] out string? propertyName)
        {
            propertyName = null;

            if (instruction.OpCode.Code != Code.Call)
                return false;

            if (instruction.GetMethodDefinition() is not MethodDefinition methodDefinition)
                return false;

            if (!(methodDefinition.IsSetter || methodDefinition.IsGetter))
                return false;

            var operandName = methodDefinition.Name;

            if (operandName?.StartsWith(prefix, StringComparison.Ordinal) != true)
                return false;

            propertyName = operandName.Substring(prefix.Length);

            return true;
        }

        private static MethodDefinition? GetMethodDefinition(this Instruction instruction) => instruction.Operand switch
        {
            MethodDefinition methodDefinition => methodDefinition,
            MethodReference methodReference => methodReference.Resolve(),
            _ => default,
        };

        public static FieldDefinition? FindAutoPropertyBackingField(this PropertyDefinition property, IEnumerable<FieldDefinition> fields)
        {
            var propertyName = property.Name;

            return fields.FirstOrDefault(field => field.Name == $"<{propertyName}>k__BackingField");
        }

        public static bool IsExtensionMethodCall(this Instruction? instruction, string? methodName)
        {
            if (instruction?.OpCode.Code != Code.Call)
                return false;

            if (instruction.Operand is not GenericInstanceMethod operand)
                return false;

            if (operand.DeclaringType?.FullName != "AutoProperties.BackingFieldAccessExtensions")
                return false;

            if (operand.Name != methodName)
                return false;

            return true;
        }

        public static IEnumerable<TypeDefinition> GetSelfAndBaseTypes(this TypeDefinition typeDefinition)
        {
            yield return typeDefinition;

            while (true)
            {
                if (typeDefinition.BaseType is not TypeReference typeReference)
                    break;

                typeDefinition = typeReference.Resolve();

                yield return typeDefinition;
            }
        }

        public static TValue? GetValueOrDefault<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, TKey? key) where TKey : class where TValue : class
        {
            return (key is not null) && dictionary.TryGetValue(key, out var value) ? value : default;
        }

        public static bool AccessesMember(this MethodDefinition method, IMemberDefinition member)
        {
            return method.Body?.Instructions?.Any(inst => inst?.Operand == member) ?? false;
        }

        public static void ReplaceFieldAccessWithPropertySetter(this MethodDefinition constructor, IMemberDefinition field, PropertyDefinition property, ISymbolReader? symbolReader)
        {
            var setMethod = property.SetMethod;

            if (setMethod is null)
                return;

            // field initializers are called before the call to the base constructor, but property setters must be called after!

            var instructions = constructor.Body?.Instructions;

            if (instructions is null)
                return;

            var instructionSequences = new InstructionSequences(instructions, constructor.ReadSequencePoints(symbolReader));

            var newInstructions = new List<Instruction>();

            foreach (var sequence in instructionSequences)
            {
                for (var i = 0; i < sequence.Count; i++)
                {
                    var instruction = sequence[i];

                    if ((instruction.OpCode != OpCodes.Stfld) || (instruction.Operand != field) || (i < 2))
                        continue;

                    sequence[i] = Instruction.Create(OpCodes.Call, setMethod);
                    newInstructions.AddRange(sequence.Take(i + 1));

                    for (var k = 0; k <= i; k++)
                    {
                        sequence.RemoveAt(0);
                    }

                    break;
                }
            }

            var index = instructions.TakeWhile(inst => !inst.IsBaseConstructorCall(constructor)).Count() + 1;

            if (index <= instructions.Count)
            {
                instructions.InsertRange(index, newInstructions.ToArray());
            }
        }

        private static bool IsBaseConstructorCall(this Instruction instruction, MethodDefinition constructor)
        {
            return (instruction.OpCode == OpCodes.Call)
                   && (instruction.Operand is MethodReference targetMethod)
                   && (targetMethod.Name == ".ctor")
                   && (targetMethod.DeclaringType.Resolve() == constructor.DeclaringType?.BaseType.Resolve());
        }

        public static FieldReference GetReference(this FieldDefinition field)
        {
            // Make the backing field - even of get-only properties - accessible by the interceptors...
            //field.IsInitOnly = false;

            var declaringType = field.DeclaringType;

            if (!declaringType.HasGenericParameters)
                return field;

            var genericDeclaringType = new GenericInstanceType(declaringType);

            genericDeclaringType.GenericArguments.AddRange(declaringType.GenericParameters);

            return new(field.Name, field.FieldType, genericDeclaringType);
        }

        public static TypeReference GetReference(this TypeReference type)
        {
            return GetReference(type, type.GenericParameters.Cast<TypeReference>().ToArray());
        }

        private static TypeReference GetReference(this TypeReference type, ICollection<TypeReference> arguments)
        {
            if (!type.HasGenericParameters)
                return type;

            if (type.GenericParameters.Count != arguments.Count)
                throw new ArgumentException("Generic parameters mismatch");

            var instance = new GenericInstanceType(type);

            foreach (var argument in arguments)
                instance.GenericArguments.Add(argument);

            return instance;
        }

        public static MethodReference GetReference(this MethodReference callee, TypeReference callingType)
        {
            var genericParameterProvider = callingType.Module.TryImportReference(callingType.Resolve()?.GetSelfAndBaseTypes().FirstOrDefault(t => t.HasGenericParameters));

            return callingType.Module.ImportReference(callee.InnerGetReference(callingType), genericParameterProvider);
        }

        private static MethodReference InnerGetReference(this MethodReference callee, TypeReference callingType)
        {
            var baseType = callingType;
            var genericParameters = callingType.GenericParameters.ToArray();
            var genericArguments = genericParameters.Cast<TypeReference>().ToArray();

            var calleeType = callee.DeclaringType.Resolve();

            while (baseType.Resolve() != calleeType)
            {
                baseType = baseType.Resolve().BaseType;

                if (baseType is null)
                    return callee;

                if (baseType is IGenericInstance genericInstance)
                {
                    var arguments = genericInstance.GenericArguments.ToArray();

                    for (var i = 0; i < arguments.Length; i++)
                    {
                        var argument = arguments[i];

                        if (!argument.ContainsGenericParameter)
                            continue;

                        var position = ((GenericParameter)argument).Position;

                        if (genericParameters.Length > position)
                        {
                            arguments[i] = genericArguments[position];
                        }
                    }

                    genericArguments = arguments;
                    genericParameters = baseType.GetElementType().GenericParameters.ToArray();
                }
            }

            if (!genericArguments.Any())
                return callee;

            var reference = new MethodReference(callee.Name, callee.ReturnType, callee.DeclaringType.GetReference(genericArguments.ToArray()))
            {
                HasThis = callee.HasThis,
                ExplicitThis = callee.ExplicitThis,
                CallingConvention = callee.CallingConvention,
            };

            reference.Parameters.AddRange(callee.Parameters.Select(parameter => new ParameterDefinition(parameter.ParameterType)));
            reference.GenericParameters.AddRange(callee.GenericParameters.Select(parameter => new GenericParameter(parameter.Name, reference)));

            return reference;
        }

        public static MethodReference? TryImportReference(this ModuleDefinition module, MethodReference? method) => (method is null) ? default : module.ImportReference(method, default);

        public static TypeReference? TryImportReference(this ModuleDefinition module, TypeReference? type) => (type is null) ? default : module.ImportReference(type, default);

        public static TypeReference ImportReference(this TypeDefinition typeDefinition, TypeReference typeReference) => typeDefinition.Module.ImportReference(typeReference, default);

        public static MethodReference ImportReference(this TypeDefinition typeDefinition, MethodReference methodReference) => typeDefinition.Module.ImportReference(methodReference, default);

        public static MethodReference MakeGeneric(this MethodReference self, TypeReference declaringType)
        {
            var reference = new MethodReference(self.Name, self.ReturnType, declaringType)
            {
                HasThis = self.HasThis,
                ExplicitThis = self.ExplicitThis,
                CallingConvention = self.CallingConvention,
            };

            foreach (var parameter in self.Parameters)
            {
                reference.Parameters.Add(new(parameter.ParameterType));
            }

            return reference;
        }

        public static bool HasInterface(this TypeDefinition typeDefinition, string fullName)
        {
            foreach (var interfaceImplementation in typeDefinition.Interfaces)
            {
                if (interfaceImplementation.InterfaceType.FullName == fullName)
                {
                    return true;
                }
            }

            return default;
        }

        public static SequencePoint? GetPoint(this MethodDefinition methodDefinition) => methodDefinition.DebugInformation.SequencePoints.FirstOrDefault();

        public static SequencePoint? GetPoint(this TypeDefinition typeDefinition)
        {
            var constructors = new List<MethodDefinition>();

            foreach (var methodDefinition in typeDefinition.Methods)
            {
                if (methodDefinition.IsConstructor)
                {
                    constructors.Add(methodDefinition);
                }
            }

            foreach (var constructor in constructors)
            {
                if (((constructor.Attributes | MethodAttributes.Static) & MethodAttributes.Static) is 0)
                {
                    return constructor.DebugInformation.SequencePoints.FirstOrDefault();
                }
            }

            foreach (var constructor in constructors)
            {
                if (((constructor.Attributes | MethodAttributes.Static) & MethodAttributes.Static) is MethodAttributes.Static)
                {
                    return constructor.DebugInformation.SequencePoints.FirstOrDefault();
                }
            }

            return default;
        }

        public static bool IsOverridden(this MethodDefinition methodDefinition)
        {
            const MethodAttributes Attributes = MethodAttributes.Virtual | MethodAttributes.HideBySig;  // override

            return (methodDefinition.Attributes & Attributes) is Attributes;
        }

        public static bool IsVirtual(this MethodDefinition methodDefinition)
        {
            const MethodAttributes Attributes = MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot;  // virtual

            return (methodDefinition.Attributes & Attributes) is Attributes;
        }

        public static bool IsAbstract(this MethodDefinition methodDefinition)
        {
            const MethodAttributes Attributes = MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Abstract;  // abstract

            return (methodDefinition.Attributes & Attributes) is Attributes;
        }

        public static bool IsStatic(this TypeDefinition type) => (type.Attributes & (TypeAttributes.Abstract | TypeAttributes.Sealed)) == (TypeAttributes.Abstract | TypeAttributes.Sealed);
    }
}
