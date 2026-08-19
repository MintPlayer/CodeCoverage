using Raven.Client.Documents.Indexes;

namespace Coverage.Indexes;

// Corax refuses to index a complex object as a single field: it throws
// NotSupportedInCoraxException ("The value of 'X' field is a complex object …")
// on every document it maps, so the index never has a single successful entry.
//
// [GenerateIndex] maps every Spark model property, nested objects and collections
// included, and always emits StoreAllFields — so an entity with any nested member
// generates an index that faults on all of its documents. There is no compile-time
// signal; it surfaces only as index errors at runtime, and the grid that now
// projects through the index returns nothing.
//
// The remedy Corax names is exactly this: indexing off, storage on — the field
// stays retrievable through the projection, so the grids keep rendering these
// columns. It costs nothing we want, because nothing filters or sorts on them.
//
// OnInitialize() runs at the end of the generated constructor, after
// StoreAllFields, which is the seam the generator provides for index configuration
// it cannot infer. If a nested member is ever added to one of these entities, its
// field must be added here too, or that index starts failing on every document.

public partial class Builds_Overview
{
    partial void OnInitialize()
    {
        Index(nameof(VBuild.Sessions), FieldIndexing.No);
        Index(nameof(VBuild.Coverage), FieldIndexing.No);
    }
}

public partial class Repositories_Overview
{
    partial void OnInitialize()
    {
        Index(nameof(VRepository.LatestCoverage), FieldIndexing.No);
    }
}
