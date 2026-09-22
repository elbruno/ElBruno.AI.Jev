namespace ElBruno.AI.Jev;

/// <summary>Official Jev typed decisions and discovery, not generative chat or embeddings.</summary>
public interface IJevDecisionClient
{
    /// <summary>Evaluates independent named questions against one shared state.</summary>
    Task<JevDecisionResponse> EvaluateAsync(JevDecisionRequest request, CancellationToken cancellationToken = default);

    /// <summary>Lists account-visible models, which may list aliases without all accepted pinned IDs.</summary>
    Task<JevModelList> ListModelsAsync(CancellationToken cancellationToken = default);
}
