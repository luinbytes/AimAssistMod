using System;
using System.Collections.Generic;
using System.Reflection;

internal static class ModReflectionHelper
{
    private const BindingFlags MemberFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static readonly object CacheLock = new object();
    private static readonly Dictionary<string, PropertyInfo> PropertyCache = new Dictionary<string, PropertyInfo>(128);
    private static readonly Dictionary<string, FieldInfo> FieldCache = new Dictionary<string, FieldInfo>(128);
    private static readonly Dictionary<string, MethodInfo> MethodCache = new Dictionary<string, MethodInfo>(128);
    private static readonly HashSet<string> LoggedResolutions = new HashSet<string>();

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

    private static MethodInfo GetCachedMethod(Type instanceType, string memberName)
    {
        string key = BuildCacheKey(instanceType, memberName);
        lock (CacheLock)
        {
            MethodInfo method;
            if (MethodCache.TryGetValue(key, out method))
            {
                return method;
            }

            method = instanceType.GetMethod(memberName, MemberFlags);
            MethodCache[key] = method;
            return method;
        }
    }

    // Defensive fallback cascades: try each candidate name in order, return the first
    // non-null match. Callers should cache the returned MemberInfo in their own fields
    // (the individual-name cache above makes repeated probes cheap anyway). Used to
    // survive game updates that rename methods/fields between patches, e.g. the
    // PlayerMovement SetYaw cascade: SetYaw -> SetAimYaw -> SetFacingAngle -> ...

    internal static MethodInfo GetMethodCascade(Type instanceType, string label, params string[] candidateNames)
    {
        if (instanceType == null || candidateNames == null || candidateNames.Length == 0)
        {
            return null;
        }

        for (int i = 0; i < candidateNames.Length; i++)
        {
            MethodInfo resolved = GetCachedMethod(instanceType, candidateNames[i]);
            if (resolved != null)
            {
                LogResolutionOnce(label, instanceType, candidateNames[i]);
                return resolved;
            }
        }

        LogMissCascadeOnce(label, instanceType, candidateNames);
        return null;
    }

    internal static FieldInfo GetFieldCascade(Type instanceType, string label, params string[] candidateNames)
    {
        if (instanceType == null || candidateNames == null || candidateNames.Length == 0)
        {
            return null;
        }

        for (int i = 0; i < candidateNames.Length; i++)
        {
            FieldInfo resolved = GetCachedField(instanceType, candidateNames[i]);
            if (resolved != null)
            {
                LogResolutionOnce(label, instanceType, candidateNames[i]);
                return resolved;
            }
        }

        LogMissCascadeOnce(label, instanceType, candidateNames);
        return null;
    }

    internal static PropertyInfo GetPropertyCascade(Type instanceType, string label, params string[] candidateNames)
    {
        if (instanceType == null || candidateNames == null || candidateNames.Length == 0)
        {
            return null;
        }

        for (int i = 0; i < candidateNames.Length; i++)
        {
            PropertyInfo resolved = GetCachedProperty(instanceType, candidateNames[i]);
            if (resolved != null)
            {
                LogResolutionOnce(label, instanceType, candidateNames[i]);
                return resolved;
            }
        }

        LogMissCascadeOnce(label, instanceType, candidateNames);
        return null;
    }

    private static void LogResolutionOnce(string label, Type instanceType, string winningName)
    {
        string key = instanceType.FullName + "::" + label + "=" + winningName;
        lock (CacheLock)
        {
            if (!LoggedResolutions.Add(key))
            {
                return;
            }
        }
        try
        {
            MelonLoader.MelonLogger.Msg($"[MimiMod] resolved {instanceType.Name}.{label} via '{winningName}'");
        }
        catch
        {
        }
    }

    private static void LogMissCascadeOnce(string label, Type instanceType, string[] candidateNames)
    {
        string key = instanceType.FullName + "::MISS::" + label;
        lock (CacheLock)
        {
            if (!LoggedResolutions.Add(key))
            {
                return;
            }
        }
        try
        {
            MelonLoader.MelonLogger.Warning(
                $"[MimiMod] {instanceType.Name}.{label} cascade missed all candidates: [{string.Join(",", candidateNames)}]");
        }
        catch
        {
        }
    }
}
