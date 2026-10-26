using System.Reflection;

namespace AmongLLM.GamePlugin;

internal static class PropertyPath
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance;

    private static string[] Parse(string path)
    {
        string[] parts = path.Split('.');
        if (path.Length > 256 || parts.Length > 8 || parts.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("A member path must contain 1 to 8 nonempty segments and at most 256 characters.");
        return parts;
    }

    public static object? Read(object root, string path)
    {
        object? current = root;
        foreach (string name in Parse(path)) current = Get(Member(current ?? throw new InvalidOperationException("Member path encountered null."), name), current!);
        return current;
    }

    public static void Write(object root, string path, Func<Type, object?> convert)
    {
        string[] parts = Parse(path);
        WriteAt(root, parts, 0, convert);
    }

    private static void WriteAt(object current, string[] parts, int index, Func<Type, object?> convert)
    {
        MemberInfo member = Member(current, parts[index]);
        Type type = member is PropertyInfo property ? property.PropertyType : ((FieldInfo)member).FieldType;
        if (index == parts.Length - 1) Set(member, current, convert(type));
        else
        {
            object child = Get(member, current) ?? throw new InvalidOperationException("Member path encountered null.");
            // Struct getters return boxed copies: write them back through every containing property.
            if (type.IsValueType) EnsureWritable(member);
            WriteAt(child, parts, index + 1, convert);
            if (type.IsValueType) Set(member, current, child);
        }
    }

    private static MemberInfo Member(object current, string name)
    {
        Type type = current.GetType();
        var property = type.GetProperty(name, Flags);
        if (property != null && property.GetIndexParameters().Length == 0) return property;
        return (MemberInfo?)type.GetField(name, Flags) ?? throw new MissingMemberException(type.FullName, name);
    }

    private static object? Get(MemberInfo member, object instance) => member switch
    {
        PropertyInfo property when property.GetMethod?.IsPublic == true => property.GetValue(instance),
        FieldInfo field => field.GetValue(instance),
        _ => throw new InvalidOperationException($"Member {member.Name} is not readable.")
    };

    private static void EnsureWritable(MemberInfo member)
    {
        if (member is PropertyInfo property && property.SetMethod?.IsPublic != true || member is FieldInfo field && field.IsInitOnly)
            throw new InvalidOperationException($"Member {member.Name} is not writable.");
    }

    private static void Set(MemberInfo member, object instance, object? value)
    {
        EnsureWritable(member);
        if (member is PropertyInfo property) property.SetValue(instance, value);
        else ((FieldInfo)member).SetValue(instance, value);
    }
}
