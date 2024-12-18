using System;

using Mono.Cecil;

namespace AutoProperties.Fody
{
    internal sealed class WeavingException(string message, MethodReference? method = default) : Exception(message)
    {
        public MethodReference? Method { get; } = method;
    }
}
