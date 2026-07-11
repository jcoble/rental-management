using System.Collections;
using System.Data.Common;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using RentalCommand.Core.Atomic;

namespace RentalCommand.Data.Atomic;

internal sealed record AtomicHandlerRegistration<TCommand, TResult>(Type HandlerType)
    where TCommand : notnull, IAtomicCommandData
    where TResult : notnull;

internal static class AtomicCommandAdmission
{
    private static readonly HashSet<Type> ScalarTypes =
    [
        typeof(string),
        typeof(decimal),
        typeof(Guid),
        typeof(DateTime),
        typeof(DateTimeOffset),
        typeof(DateOnly),
        typeof(TimeOnly),
        typeof(TimeSpan),
        typeof(Uri),
    ];

    public static void ValidateCommand(IAtomicCommandData command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateCommandValue(command, new HashSet<object>(ReferenceEqualityComparer.Instance));
    }

    public static void ValidateResultType(Type resultType)
    {
        ArgumentNullException.ThrowIfNull(resultType);
        ValidateResultType(resultType, "result type", []);
    }

    public static void ValidateResult(object result)
    {
        ArgumentNullException.ThrowIfNull(result);
        ValidateResultValue(result, new HashSet<object>(ReferenceEqualityComparer.Instance));
    }

    public static void ValidateHandler(Type handlerType)
    {
        ArgumentNullException.ThrowIfNull(handlerType);
        ValidateConstructorGraph(handlerType, handlerType, []);
    }

    private static void ValidateCommandValue(object value, HashSet<object> visited)
    {
        var type = value.GetType();
        if (IsScalar(type))
        {
            ValidateCommandType(type, "command input", []);
            return;
        }

        if (!type.IsValueType && !visited.Add(value))
        {
            return;
        }

        if (value is IEnumerable sequence && value is not string)
        {
            RejectForbiddenType(type, "command collection value");
            if (!IsAllowedRuntimeCollection(type))
            {
                throw new AtomicArchitectureException(
                    $"Atomic command collection type {type.FullName} is not an approved data-only shape.");
            }

            foreach (var item in sequence)
            {
                if (item is not null)
                {
                    ValidateCommandValue(item, visited);
                }
            }

            return;
        }

        ValidateCommandType(type, "command input", []);
        if (value is not IAtomicCommandData)
        {
            throw new AtomicArchitectureException(
                $"Atomic command member type {type.FullName} must implement {nameof(IAtomicCommandData)}.");
        }

        foreach (var field in InstanceFields(type))
        {
            if (field.GetValue(value) is { } memberValue)
            {
                ValidateCommandValue(memberValue, visited);
            }
        }
    }

    private static void ValidateCommandType(Type type, string location, HashSet<Type> path)
    {
        RejectForbiddenType(type, location);
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (IsScalar(type))
        {
            return;
        }

        if (type.IsArray)
        {
            ValidateCommandType(type.GetElementType()!, $"{location} element", path);
            return;
        }

        if (IsAllowedCollection(type))
        {
            foreach (var argument in type.GetGenericArguments())
            {
                ValidateCommandType(argument, $"{location} element", path);
            }

            return;
        }

        if (!typeof(IAtomicCommandData).IsAssignableFrom(type))
        {
            throw new AtomicArchitectureException(
                $"Atomic {location} has non-data type {type.FullName}; custom command DTOs must implement " +
                $"{nameof(IAtomicCommandData)}.");
        }

        if (!type.IsValueType && (!type.IsSealed || type.IsAbstract || type.IsInterface))
        {
            throw new AtomicArchitectureException(
                $"Atomic {location} {type.FullName} must be a sealed concrete data-only type.");
        }

        if (!path.Add(type))
        {
            return;
        }

        foreach (var field in InstanceFields(type))
        {
            ValidateCommandType(field.FieldType, $"command member {type.Name}.{field.Name}", path);
        }

        foreach (var property in PublicInstanceProperties(type))
        {
            ValidateDataProperty(property, "command");
            ValidateCommandType(property.PropertyType, $"command member {type.Name}.{property.Name}", path);
        }

        path.Remove(type);
    }

    private static void ValidateResultValue(object value, HashSet<object> visited)
    {
        var type = value.GetType();
        if (IsScalar(type))
        {
            ValidateResultType(type, "result value", []);
            return;
        }

        if (!type.IsValueType && !visited.Add(value))
        {
            return;
        }

        if (value is IEnumerable sequence && value is not string)
        {
            RejectForbiddenType(type, "result collection value");
            if (!IsAllowedRuntimeCollection(type))
            {
                throw new AtomicArchitectureException(
                    $"Atomic result collection type {type.FullName} is not an approved data-only shape.");
            }

            foreach (var item in sequence)
            {
                if (item is not null)
                {
                    ValidateResultValue(item, visited);
                }
            }

            return;
        }

        ValidateResultType(type, "result value", []);
        foreach (var field in InstanceFields(type))
        {
            if (field.GetValue(value) is { } memberValue)
            {
                ValidateResultValue(memberValue, visited);
            }
        }
    }

    private static void ValidateResultType(Type type, string location, HashSet<Type> path)
    {
        RejectForbiddenType(type, location);
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (IsScalar(type))
        {
            return;
        }

        if (type.IsArray)
        {
            ValidateResultType(type.GetElementType()!, $"{location} element", path);
            return;
        }

        if (IsAllowedCollection(type))
        {
            foreach (var argument in type.GetGenericArguments())
            {
                ValidateResultType(argument, $"{location} element", path);
            }

            return;
        }

        if (!typeof(IAtomicResultData).IsAssignableFrom(type))
        {
            throw new AtomicArchitectureException(
                $"Atomic {location} {type.FullName} must be a scalar, approved collection, or implement " +
                $"{nameof(IAtomicResultData)}.");
        }

        if (!type.IsValueType && (!type.IsSealed || type.IsAbstract || type.IsInterface))
        {
            throw new AtomicArchitectureException(
                $"Atomic {location} {type.FullName} must be a sealed concrete data-only type.");
        }

        if (!path.Add(type))
        {
            return;
        }

        foreach (var field in InstanceFields(type))
        {
            ValidateResultType(field.FieldType, $"result member {type.Name}.{field.Name}", path);
        }

        foreach (var property in PublicInstanceProperties(type))
        {
            ValidateDataProperty(property, "result");
            ValidateResultType(property.PropertyType, $"result member {type.Name}.{property.Name}", path);
        }

        path.Remove(type);
    }

    private static void ValidateConstructorGraph(
        Type type,
        Type handlerType,
        HashSet<Type> path)
    {
        RejectForbiddenType(type, $"handler {handlerType.Name} dependency");
        if (!path.Add(type))
        {
            return;
        }

        var constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        if (constructors.Length != 1)
        {
            throw new AtomicArchitectureException(
                $"Atomic handler dependency {type.FullName} must expose exactly one public constructor.");
        }

        foreach (var parameter in constructors[0].GetParameters())
        {
            var dependency = parameter.ParameterType;
            RejectForbiddenType(dependency, $"handler {handlerType.Name} dependency");
            if (dependency == typeof(IAtomicUnitOfWork))
            {
                continue;
            }

            if (!dependency.IsClass
                || dependency.IsAbstract
                || !typeof(IAtomicTransactionSafeDependency).IsAssignableFrom(dependency))
            {
                throw new AtomicArchitectureException(
                    $"Atomic handler {handlerType.Name} dependency {dependency.FullName} is not explicitly " +
                    $"allowlisted by {nameof(IAtomicTransactionSafeDependency)}.");
            }

            ValidateConstructorGraph(dependency, handlerType, path);
        }

        foreach (var field in InstanceFields(type))
        {
            var dependency = field.FieldType;
            RejectForbiddenType(dependency, $"handler {handlerType.Name} dependency field {field.Name}");
            if (dependency.IsClass
                && !dependency.IsAbstract
                && typeof(IAtomicTransactionSafeDependency).IsAssignableFrom(dependency))
            {
                ValidateConstructorGraph(dependency, handlerType, path);
            }
        }

        path.Remove(type);
    }

    private static void RejectForbiddenType(Type type, string location)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (typeof(IAtomicRemoteDependency).IsAssignableFrom(type)
            || typeof(HttpClient).IsAssignableFrom(type)
            || typeof(HttpMessageHandler).IsAssignableFrom(type)
            || type.FullName == "System.Net.Http.IHttpClientFactory"
            || typeof(IServiceProvider).IsAssignableFrom(type)
            || typeof(Delegate).IsAssignableFrom(type)
            || typeof(DbContext).IsAssignableFrom(type)
            || typeof(DatabaseFacade).IsAssignableFrom(type)
            || typeof(DbConnection).IsAssignableFrom(type)
            || typeof(DbTransaction).IsAssignableFrom(type)
            || typeof(IQueryable).IsAssignableFrom(type)
            || type == typeof(Type))
        {
            throw new AtomicArchitectureException(
                $"Atomic {location} cannot use service or infrastructure type {type.FullName}.");
        }
    }

    private static bool IsScalar(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsPrimitive || type.IsEnum || ScalarTypes.Contains(type);
    }

    private static bool IsAllowedCollection(Type type)
    {
        if (type.IsArray)
        {
            return true;
        }

        if (!type.IsGenericType)
        {
            return false;
        }

        var definition = type.GetGenericTypeDefinition();
        return definition == typeof(List<>)
            || definition == typeof(IReadOnlyList<>)
            || definition == typeof(IReadOnlyCollection<>)
            || definition == typeof(IEnumerable<>)
            || definition == typeof(HashSet<>);
    }

    private static bool IsAllowedRuntimeCollection(Type type)
    {
        if (IsAllowedCollection(type))
        {
            return true;
        }

        // C# collection expressions can materialize as internal CoreLib read-only list types
        // (for example <>z__ReadOnlySingleElementList<T>) even when the declared command member is
        // IReadOnlyList<T>. Admit only framework-owned implementations of an already approved
        // collection interface; arbitrary application-defined enumerable objects remain rejected.
        return type.Assembly == typeof(List<>).Assembly
            && type.GetInterfaces().Any(IsAllowedCollection);
    }

    private static IEnumerable<FieldInfo> InstanceFields(Type type)
    {
        for (var current = type; current is not null && current != typeof(object); current = current.BaseType)
        {
            foreach (var field in current.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                yield return field;
            }
        }
    }

    private static IEnumerable<PropertyInfo> PublicInstanceProperties(Type type) =>
        type.GetProperties(BindingFlags.Instance | BindingFlags.Public);

    private static void ValidateDataProperty(PropertyInfo property, string subject)
    {
        if (property.GetIndexParameters().Length != 0 || property.GetMethod is null)
        {
            throw new AtomicArchitectureException(
                $"Atomic {subject} property {property.DeclaringType?.Name}.{property.Name} is not a readable data property.");
        }
    }
}
