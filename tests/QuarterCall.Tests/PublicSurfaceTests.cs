using Xunit;
using System.Reflection;

namespace QuarterCall.Tests;

public sealed class PublicSurfaceTests
{
    private static readonly Assembly Package = typeof(QuarterCallClient).Assembly;

    private const BindingFlags Visible =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
        | BindingFlags.DeclaredOnly;

    [Fact]
    public void The_assembly_exports_exactly_the_documented_types()
    {
        string[] expected =
        [
            "Account", "AccountFilter", "AccountText", "AccountTextFilter", "DataClient",
            "DataIntegrityException", "DataUnavailableException", "EvidenceTable", "FilingProfile",
            "FilingQuorum", "OutstandingFiler", "PublishedFile", "PublishedQuarter", "Quarter",
            "QuarterCallClient", "QuarterCallException", "QuarterCallOptions", "QuarterJsonConverter",
            "QuarterStatus", "QuorumStatus", "QuorumVerdict", "ProfileFilter", "UnsupportedSchemaException",
        ];

        var actual = Package.GetExportedTypes().Select(t => t.FullName).Order().ToArray();

        Assert.Equal(expected.Select(n => "QuarterCall." + n).Order().ToArray(), actual);
        Assert.Equal(23, actual.Length);
    }

    [Fact]
    public void No_exposed_member_uses_a_type_from_another_package()
    {
        var foreign = new List<string>();
        foreach (var type in Package.GetExportedTypes())
        {
            Check(type.BaseType, type.FullName + " base type", foreign);
            foreach (var implemented in type.GetInterfaces())
            {
                Check(implemented, type.FullName + " interface", foreign);
            }

            foreach (var member in type.GetMembers(Visible).Where(IsExposed))
            {
                foreach (var used in TypesOf(member))
                {
                    Check(used, $"{type.FullName}.{member.Name}", foreign);
                }
            }
        }

        Assert.Empty(foreign);
    }

    private static bool IsExposed(MemberInfo member) => member switch
    {
        MethodBase m => m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly,
        FieldInfo f => f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly,
        PropertyInfo p => (p.GetMethod ?? p.SetMethod) is { } a && (a.IsPublic || a.IsFamily || a.IsFamilyOrAssembly),
        EventInfo e => e.AddMethod is { } a && (a.IsPublic || a.IsFamily || a.IsFamilyOrAssembly),
        _ => false,
    };

    private static IEnumerable<Type> TypesOf(MemberInfo member)
    {
        switch (member)
        {
            case MethodBase method:
                foreach (var parameter in method.GetParameters())
                {
                    yield return parameter.ParameterType;
                }

                if (method is MethodInfo info)
                {
                    yield return info.ReturnType;
                }

                break;
            case FieldInfo field:
                yield return field.FieldType;
                break;
            case PropertyInfo property:
                yield return property.PropertyType;
                foreach (var parameter in property.GetIndexParameters())
                {
                    yield return parameter.ParameterType;
                }

                break;
            case EventInfo @event:
                yield return @event.EventHandlerType!;
                break;
        }
    }

    // A type is the framework's or this assembly's; arrays, pointers and generic arguments are looked into.
    private static void Check(Type? type, string where, List<string> foreign)
    {
        if (type is null)
        {
            return;
        }

        if (type.HasElementType)
        {
            Check(type.GetElementType(), where, foreign);
            return;
        }

        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments())
            {
                Check(argument, where, foreign);
            }

            type = type.GetGenericTypeDefinition();
        }

        if (type.IsGenericParameter || type.Assembly == Package || IsFramework(type.Assembly))
        {
            return;
        }

        foreign.Add($"{where}: {type.FullName} from {type.Assembly.GetName().Name}");
    }

    private static bool IsFramework(Assembly assembly) =>
        assembly == typeof(object).Assembly
        || assembly.GetName().Name is { } name
        && (name.StartsWith("System.", StringComparison.Ordinal) || name == "System"
            || name == "netstandard" || name == "mscorlib" || name == "Microsoft.Win32.Primitives");
}
