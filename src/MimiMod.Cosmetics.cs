using MelonLoader;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

public partial class MimiMod
{
    private readonly string[] cosmeticUnlockFlagNames = new string[]
    {
        "everythingUnlocked",
        "forceUnlocked",
        "isCursorForceUnlocked"
    };

    private readonly string[] cosmeticRefreshMethodNames = new string[]
    {
        "LoadCosmetics",
        "SaveCosmetics",
        "ListCosmeticsUnlocks",
        "RefreshOptions",
        "UpdateCosmeticsButtons",
        "UpdateLoadoutToggles"
    };

    private void UnlockAllCosmetics()
    {
        bool anyFlagApplied = false;
        bool anyRefreshInvoked = false;

        try
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Assembly assembly = assemblies[i];

                Type managerType = assembly.GetType("CosmeticsUnlocksManager");
                if (managerType != null)
                {
                    anyFlagApplied |= ApplyCosmeticsUnlocksToType(managerType);
                    anyRefreshInvoked |= InvokeCosmeticsRefreshMethods(managerType);
                }

                Type playerCosmeticsType = assembly.GetType("PlayerCosmetics");
                if (playerCosmeticsType != null)
                {
                    anyFlagApplied |= ApplyCosmeticsUnlocksToType(playerCosmeticsType);
                    anyRefreshInvoked |= InvokeCosmeticsRefreshMethods(playerCosmeticsType);
                }

                Type customizationMenuType = assembly.GetType("PlayerCustomizationMenu");
                if (customizationMenuType != null)
                {
                    anyFlagApplied |= ApplyCosmeticsUnlocksToType(customizationMenuType);
                    anyRefreshInvoked |= InvokeCosmeticsRefreshMethods(customizationMenuType);
                }
            }

            Component[] allComponents = FindAllComponents();
            for (int i = 0; i < allComponents.Length; i++)
            {
                Component component = allComponents[i];
                if (component == null)
                {
                    continue;
                }

                string typeName = component.GetType().Name;
                if (typeName.IndexOf("Cosmetic", StringComparison.OrdinalIgnoreCase) < 0 &&
                    typeName.IndexOf("Customization", StringComparison.OrdinalIgnoreCase) < 0 &&
                    typeName.IndexOf("Unlock", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                anyFlagApplied |= ApplyCosmeticsUnlocksToObject(component, component.GetType());
                anyRefreshInvoked |= InvokeCosmeticsRefreshMethods(component, component.GetType());
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Warning("[Mimi] Cosmetic unlock failed: " + ex.Message);
        }

        if (anyFlagApplied)
        {
            MelonLogger.Msg("[Mimi] Cosmetics unlock applied.");
        }
        else if (anyRefreshInvoked)
        {
            MelonLogger.Msg("[Mimi] Cosmetics UI refresh triggered.");
        }
        else
        {
            MelonLogger.Warning("[Mimi] Cosmetics manager not found.");
        }
    }

    private bool ApplyCosmeticsUnlocksToType(Type type)
    {
        if (type == null)
        {
            return false;
        }

        bool changed = false;
        changed |= ApplyCosmeticsUnlocksToObject(null, type);

        object singleton = ResolveSingletonInstance(type);
        if (singleton != null)
        {
            changed |= ApplyCosmeticsUnlocksToObject(singleton, type);
        }

        return changed;
    }

    private bool ApplyCosmeticsUnlocksToObject(object target, Type type)
    {
        if (type == null)
        {
            return false;
        }

        bool changed = false;
        for (int i = 0; i < cosmeticUnlockFlagNames.Length; i++)
        {
            changed |= TrySetBoolMember(type, target, cosmeticUnlockFlagNames[i], true);
        }

        return changed;
    }

    private bool InvokeCosmeticsRefreshMethods(Type type)
    {
        if (type == null)
        {
            return false;
        }

        bool invoked = false;
        invoked |= InvokeCosmeticsRefreshMethods(null, type);

        object singleton = ResolveSingletonInstance(type);
        if (singleton != null)
        {
            invoked |= InvokeCosmeticsRefreshMethods(singleton, type);
        }

        return invoked;
    }

    private bool InvokeCosmeticsRefreshMethods(object target, Type type)
    {
        if (type == null)
        {
            return false;
        }

        bool invoked = false;
        for (int i = 0; i < cosmeticRefreshMethodNames.Length; i++)
        {
            invoked |= InvokeParameterlessMethodIfExists(type, target, cosmeticRefreshMethodNames[i]);
        }

        return invoked;
    }

    private object ResolveSingletonInstance(Type type)
    {
        if (type == null)
        {
            return null;
        }

        BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        string[] memberNames =
        {
            "Instance",
            "instance",
            "Singleton",
            "singleton",
            "Current",
            "current"
        };

        for (int i = 0; i < memberNames.Length; i++)
        {
            try
            {
                PropertyInfo property = type.GetProperty(memberNames[i], flags);
                if (property != null &&
                    property.GetIndexParameters().Length == 0 &&
                    property.GetMethod != null &&
                    property.GetMethod.IsStatic)
                {
                    object instance = property.GetValue(null, null);
                    if (instance != null)
                    {
                        return instance;
                    }
                }
            }
            catch
            {
            }

            try
            {
                FieldInfo field = type.GetField(memberNames[i], flags);
                if (field != null && field.IsStatic)
                {
                    object instance = field.GetValue(null);
                    if (instance != null)
                    {
                        return instance;
                    }
                }
            }
            catch
            {
            }
        }

        Component[] allComponents = FindAllComponents();
        for (int i = 0; i < allComponents.Length; i++)
        {
            Component component = allComponents[i];
            if (component != null && component.GetType() == type)
            {
                return component;
            }
        }

        return null;
    }

    private bool TrySetBoolMember(Type type, object target, string memberName, bool value)
    {
        if (type == null || string.IsNullOrEmpty(memberName))
        {
            return false;
        }

        BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        try
        {
            FieldInfo field = type.GetField(memberName, flags);
            if (field != null && field.FieldType == typeof(bool))
            {
                object actualTarget = field.IsStatic ? null : target;
                if (field.IsStatic || actualTarget != null)
                {
                    field.SetValue(actualTarget, value);
                    return true;
                }
            }
        }
        catch
        {
        }

        try
        {
            PropertyInfo property = type.GetProperty(memberName, flags);
            if (property != null &&
                property.PropertyType == typeof(bool) &&
                property.CanWrite &&
                property.GetIndexParameters().Length == 0)
            {
                MethodInfo setMethod = property.GetSetMethod(true);
                object actualTarget = setMethod != null && setMethod.IsStatic ? null : target;
                if ((setMethod != null && setMethod.IsStatic) || actualTarget != null)
                {
                    property.SetValue(actualTarget, value, null);
                    return true;
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private bool InvokeParameterlessMethodIfExists(Type type, object target, string methodName)
    {
        if (type == null || string.IsNullOrEmpty(methodName))
        {
            return false;
        }

        BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        try
        {
            MethodInfo method = type.GetMethod(methodName, flags, null, Type.EmptyTypes, null);
            if (method == null)
            {
                return false;
            }

            object actualTarget = method.IsStatic ? null : target;
            if (!method.IsStatic && actualTarget == null)
            {
                return false;
            }

            method.Invoke(actualTarget, null);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
