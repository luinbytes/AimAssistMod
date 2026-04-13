using System;
using System.Collections.Generic;
using System.Reflection;

internal static class ModReflectionHelper
{
    private const BindingFlags MemberFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static readonly object CacheLock = new object();
    private static readonly Dictionary<string, PropertyInfo> PropertyCache = new Dictionary<string, PropertyInfo>(128);
    private static readonly Dictionary<string, FieldInfo> FieldCache = new Dictionary<string, FieldInfo>(128);

    private static string BuildCacheKey(Type instanceType, string memberName)
    {
        return instanceType.AssemblyQualifiedName + "|" + memberName;
    }

    private static PropertyInfo GetCachedProperty(Type instanceType, string memberName)
    {
        string key = BuildCacheKey(instanceType, memberName);
        lock (CacheLock)
        {
            PropertyInfo property;
            if (PropertyCache.TryGetValue(key, out property))
            {
                return property;
            }

            property = instanceType.GetProperty(memberName, MemberFlags);
            PropertyCache[key] = property;
            return property;
        }
    }

    private static FieldInfo GetCachedField(Type instanceType, string memberName)
    {
        string key = BuildCacheKey(instanceType, memberName);
        lock (CacheLock)
        {
            FieldInfo field;
            if (FieldCache.TryGetValue(key, out field))
            {
                return field;
            }

            field = instanceType.GetField(memberName, MemberFlags);
            FieldCache[key] = field;
            return field;
        }
    }

    internal static float GetFloatMemberValue(object instance, string memberName, float fallbackValue)
    {
        if (instance == null)
        {
            return fallbackValue;
        }

        Type instanceType = instance.GetType();

        try
        {
            PropertyInfo property = GetCachedProperty(instanceType, memberName);
            if (property != null)
            {
                object propertyValue = property.GetValue(instance, null);
                if (propertyValue is float)
                {
                    return (float)propertyValue;
                }
                if (propertyValue is double)
                {
                    return (float)(double)propertyValue;
                }
                if (propertyValue is int)
                {
                    return (int)propertyValue;
                }
            }
        }
        catch
        {
        }

        try
        {
            FieldInfo field = GetCachedField(instanceType, memberName);
            if (field != null)
            {
                object fieldValue = field.GetValue(instance);
                if (fieldValue is float)
                {
                    return (float)fieldValue;
                }
                if (fieldValue is double)
                {
                    return (float)(double)fieldValue;
                }
                if (fieldValue is int)
                {
                    return (int)fieldValue;
                }
            }
        }
        catch
        {
        }

        return fallbackValue;
    }

    internal static object GetMemberValue(object instance, string memberName)
    {
        if (instance == null)
        {
            return null;
        }

        Type instanceType = instance.GetType();

        try
        {
            PropertyInfo property = GetCachedProperty(instanceType, memberName);
            if (property != null)
            {
                return property.GetValue(instance, null);
            }
        }
        catch
        {
        }

        try
        {
            FieldInfo field = GetCachedField(instanceType, memberName);
            if (field != null)
            {
                return field.GetValue(instance);
            }
        }
        catch
        {
        }

        return null;
    }
}
