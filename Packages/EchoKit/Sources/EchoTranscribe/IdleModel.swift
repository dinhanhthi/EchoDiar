/// The recording transcriber's model, loaded on demand: `releaseIfIdle` frees it once it went unused for `idle` (no
/// chunk comes while a recording is paused), and the next `get` loads it again. Used by one decode task only.
final class IdleModel<Model> {
    private let idle: Duration?
    private let load: () async throws -> Model
    private let unload: (Model) async -> Void
    private var model: Model?
    private var lastUse: ContinuousClock.Instant

    /// `model` is already loaded; `idle` nil keeps it until `release()`.
    init(
        _ model: Model, idle: Duration?, now: ContinuousClock.Instant = .now,
        load: @escaping () async throws -> Model, unload: @escaping (Model) async -> Void
    ) {
        self.model = model
        self.idle = idle
        self.load = load
        self.unload = unload
        lastUse = now
    }

    /// The model, loaded again when it was freed; counts as a use.
    func get(now: ContinuousClock.Instant = .now) async throws -> Model {
        lastUse = now
        if let model { return model }
        let loaded = try await load()
        model = loaded
        return loaded
    }

    /// Frees the model once `idle` passed since its last use; true when it did.
    @discardableResult
    func releaseIfIdle(now: ContinuousClock.Instant = .now) async -> Bool {
        guard let idle, let model, now - lastUse >= idle else { return false }
        self.model = nil
        await unload(model)
        return true
    }

    func release() async {
        guard let model else { return }
        self.model = nil
        await unload(model)
    }
}
