using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Api.Common;

/// <summary>Where the next page of a list ordered by name starts: pass both values back as <c>after</c> and <c>afterId</c>.</summary>
public sealed record NameCursor(string After, Guid AfterId);

/// <summary>Where the next page of a newest-first list starts: pass both values back as <c>before</c> and <c>beforeId</c>, as for the audit log.</summary>
public sealed record TimeCursor(DateTimeOffset Before, Guid BeforeId);

/// <summary>One page of a list, and where the next one starts. <see cref="Next"/> is null on the last page.</summary>
public sealed record PagedResponse<TItem, TCursor>(IReadOnlyList<TItem> Items, TCursor? Next)
    where TCursor : class;

/// <summary>The rows of one page, before they are turned into the response, and the cursor of the page after it.</summary>
public sealed record PageRows<TRow, TCursor>(IReadOnlyList<TRow> Rows, TCursor? Next)
    where TCursor : class
{
    public PagedResponse<TItem, TCursor> Map<TItem>(Func<IReadOnlyList<TRow>, IReadOnlyList<TItem>> map) => new(map(Rows), Next);
}

/// <summary>
/// The paging every list endpoint takes (PLT-08): <c>limit</c> (default 50, at most 200) and the cursor the previous page returned.
/// A list ordered by name takes <c>after</c> with <c>afterId</c>; a newest-first list takes <c>before</c> with <c>beforeId</c>, exactly as
/// the audit log does. With no paging parameters the first page is returned. A cursor holds only the position in the list, never a
/// tenant or a row, so a cursor from another tenant just starts somewhere in this tenant's own rows.
/// </summary>
public sealed record PageQuery(int? Limit, string? After, Guid? AfterId, DateTimeOffset? Before, Guid? BeforeId)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;
    private const int MaxKeyLength = 512;

    /// <summary>The rows to read: one more than the limit, to know whether there is a next page.</summary>
    public int Take => Limit ?? DefaultLimit;

    /// <summary>Why the paging parameters are unusable, or null when they are fine. Returned as a 400.</summary>
    public IResult? Problem(bool byName)
    {
        var errors = new Dictionary<string, string[]>();

        if (Limit is < 1 or > MaxLimit)
        {
            errors["limit"] = [$"Limit must be between 1 and {MaxLimit}."];
        }

        if (byName)
        {
            if ((After is null) != (AfterId is null))
            {
                errors["after"] = ["Pass after and afterId together, exactly as the previous page returned them."];
            }
            else if (After is { Length: 0 } or { Length: > MaxKeyLength })
            {
                errors["after"] = ["That is not a cursor this list returned."];
            }
        }
        else if ((Before is null) != (BeforeId is null))
        {
            errors["before"] = ["Pass before and beforeId together, exactly as the previous page returned them."];
        }

        return errors.Count == 0 ? null : Results.ValidationProblem(errors);
    }

    /// <summary>A list ordered by name, then id, from the cursor on.</summary>
    public async Task<PageRows<TRow, NameCursor>> ByNameAsync<TRow>(
        IQueryable<TRow> source,
        Expression<Func<TRow, string>> name,
        Expression<Func<TRow, Guid>> id,
        CancellationToken cancellationToken)
    {
        if (After is { } after && AfterId is { } afterId)
        {
            var idBody = SameParameter(id, name.Parameters[0]);
            var nameIsAfter = Expression.GreaterThan(Expression.Call(CompareStrings, name.Body, Held(after)), Expression.Constant(0));
            var nameIsSame = Expression.Equal(name.Body, Held(after));
            var idIsAfter = Expression.GreaterThan(Expression.Call(idBody, CompareGuids, Held(afterId)), Expression.Constant(0));

            var predicate = Expression.OrElse(nameIsAfter, Expression.AndAlso(nameIsSame, idIsAfter));
            source = source.Where(Expression.Lambda<Func<TRow, bool>>(predicate, name.Parameters[0]));
        }

        var rows = await source.OrderBy(name).ThenBy(id).Take(Take + 1).ToListAsync(cancellationToken);
        var page = rows.Take(Take).ToList();
        var next = rows.Count > Take ? new NameCursor(name.Compile()(page[^1]), id.Compile()(page[^1])) : null;
        return new PageRows<TRow, NameCursor>(page, next);
    }

    /// <summary>A newest-first list, by time and then id, from the cursor on.</summary>
    public async Task<PageRows<TRow, TimeCursor>> NewestFirstAsync<TRow>(
        IQueryable<TRow> source,
        Expression<Func<TRow, DateTimeOffset>> time,
        Expression<Func<TRow, Guid>> id,
        CancellationToken cancellationToken)
    {
        if (Before is { } before && BeforeId is { } beforeId)
        {
            var idBody = SameParameter(id, time.Parameters[0]);
            var timeIsBefore = Expression.LessThan(time.Body, Held(before));
            var timeIsSame = Expression.Equal(time.Body, Held(before));
            var idIsBefore = Expression.LessThan(Expression.Call(idBody, CompareGuids, Held(beforeId)), Expression.Constant(0));

            var predicate = Expression.OrElse(timeIsBefore, Expression.AndAlso(timeIsSame, idIsBefore));
            source = source.Where(Expression.Lambda<Func<TRow, bool>>(predicate, time.Parameters[0]));
        }

        var rows = await source.OrderByDescending(time).ThenByDescending(id).Take(Take + 1).ToListAsync(cancellationToken);
        var page = rows.Take(Take).ToList();
        var next = rows.Count > Take ? new TimeCursor(time.Compile()(page[^1]), id.Compile()(page[^1])) : null;
        return new PageRows<TRow, TimeCursor>(page, next);
    }

    private static readonly MethodInfo CompareStrings = typeof(string).GetMethod(nameof(string.Compare), [typeof(string), typeof(string)])!;
    private static readonly MethodInfo CompareGuids = typeof(Guid).GetMethod(nameof(Guid.CompareTo), [typeof(Guid)])!;

    /// <summary>
    /// The cursor's value as a member of an object, not a constant: the database then gets it as a parameter and one cached plan serves
    /// every page, instead of a new query for every cursor.
    /// </summary>
    private static MemberExpression Held<T>(T value) => Expression.Property(Expression.Constant(new CursorValue<T>(value)), nameof(CursorValue<T>.Value));

    /// <summary>The body of <paramref name="selector"/> written in terms of <paramref name="parameter"/>, so two selectors can share one lambda.</summary>
    private static Expression SameParameter<TRow, TValue>(Expression<Func<TRow, TValue>> selector, ParameterExpression parameter) =>
        selector.Parameters[0] == parameter ? selector.Body : new ParameterReplacer(selector.Parameters[0], parameter).Visit(selector.Body);

    private sealed class ParameterReplacer(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
    }
}

internal sealed class CursorValue<T>(T value)
{
    public T Value { get; } = value;
}
