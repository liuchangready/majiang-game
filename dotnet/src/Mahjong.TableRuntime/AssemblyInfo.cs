using System.Runtime.CompilerServices;

// Deterministic dispatch-scheduling probes need the internal channel test seam and internal
// handle constructors. This is test-only visibility; the public TableRuntime surface is unchanged.
[assembly: InternalsVisibleTo("Mahjong.TableRuntime.Tests")]
