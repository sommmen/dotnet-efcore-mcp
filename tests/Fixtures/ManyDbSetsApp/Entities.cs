namespace ManyDbSetsApp;

/// <summary>Primary entity used for direct query assertions.</summary>
public class Widget
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

/// <summary>Second entity, referenced together with <see cref="Widget"/> by name in a query
/// (e.g. <c>Union</c>) to prove cross-DbSet lambda-parameter registration still works once the
/// context also exposes many unrelated DbSets.</summary>
public class Gadget
{
    public int Id { get; set; }

    public string Label { get; set; } = string.Empty;
}

// The Filler* entities below exist only to pad the context's total public DbSet count well past
// 16 - the point at which System.Linq.Dynamic.Core's ParseLambda can no longer reuse a built-in
// Func<>/Action<> delegate type and must instead emit a custom one via Reflection.Emit into a
// non-collectible dynamic assembly. That emitted delegate cannot reference entity types loaded
// into this context's collectible AssemblyLoadContext, so QueryExecutor must never register more
// lambda parameters than the query text actually references. See
// docs/development/query-execution.md and QueryExecutor.GetOtherDbSetProperties's caller.
public class Filler01 { public int Id { get; set; } }
public class Filler02 { public int Id { get; set; } }
public class Filler03 { public int Id { get; set; } }
public class Filler04 { public int Id { get; set; } }
public class Filler05 { public int Id { get; set; } }
public class Filler06 { public int Id { get; set; } }
public class Filler07 { public int Id { get; set; } }
public class Filler08 { public int Id { get; set; } }
public class Filler09 { public int Id { get; set; } }
public class Filler10 { public int Id { get; set; } }
public class Filler11 { public int Id { get; set; } }
public class Filler12 { public int Id { get; set; } }
public class Filler13 { public int Id { get; set; } }
public class Filler14 { public int Id { get; set; } }
public class Filler15 { public int Id { get; set; } }
public class Filler16 { public int Id { get; set; } }
public class Filler17 { public int Id { get; set; } }
public class Filler18 { public int Id { get; set; } }
public class Filler19 { public int Id { get; set; } }
public class Filler20 { public int Id { get; set; } }
