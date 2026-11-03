using Xunit;

// Three PostgreSQL integration classes apply EF migrations. Serialize test classes so a
// fresh, empty test database is migrated once before the other classes inspect it.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
