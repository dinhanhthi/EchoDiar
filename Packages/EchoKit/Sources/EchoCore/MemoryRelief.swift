import Darwin

/// Gives freed heap pages back to the system. Unloading a model frees its memory, but the allocator keeps the pages
/// for reuse, so the footprint stays near its peak until memory pressure: call this once a model is gone.
public enum MemoryRelief {
    public static func trim() {
        _ = malloc_zone_pressure_relief(nil, 0)
    }
}
