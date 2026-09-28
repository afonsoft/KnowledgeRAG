namespace KnowledgeHub.Server.Embeddings;

/// <summary>Configuration for <see cref="IEmbeddingProvider"/> selection (SPEC-03 RF-003).</summary>
public sealed class EmbeddingOptions
{
    public const string SectionName = "Embeddings";

    /// <summary>deterministic | ollama | openai | voyage | cohere | onnx</summary>
    public string Provider { get; set; } = "deterministic";

    /// <summary>Base URL — e.g. http://localhost:11434 (Ollama) or https://api.openai.com.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Bearer key for OpenAI-compatible providers. Never logged.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Model name sent to the provider (e.g. nomic-embed-text).</summary>
    public string? Model { get; set; }

    /// <summary>Expected vector length; provider output is validated/sliced to this.</summary>
    public int Dimensions { get; set; } = 384;

    /// <summary>Directory holding model.onnx + vocab.txt for <c>Provider=onnx</c>
    /// (SPEC-20260917-onnx-local-embeddings RF-002). Relative paths resolve
    /// against the working directory; default <c>models/all-MiniLM-L6-v2</c>.</summary>
    public string? ModelPath { get; set; }

    /// <summary>SPEC-20260924-asymmetric-embeddings: role-aware embeddings
    /// (query vs document). Enabling on an existing corpus requires reindex —
    /// document vectors produced without prefixes are not comparable.</summary>
    public AsymmetricOptions Asymmetric { get; set; } = new();

    /// <summary>OpenAI-compatible <c>input_type</c> sent with query embeddings
    /// (providers that support it, e.g. Voyage via compatible endpoint).</summary>
    public string? QueryInputType { get; set; }

    /// <summary>OpenAI-compatible <c>input_type</c> sent with document embeddings.</summary>
    public string? DocumentInputType { get; set; }

    /// <summary>Voyage AI provider configuration (SPEC-20260927-voyage-and-cohere-embeddings).</summary>
    public VoyageOptions Voyage { get; set; } = new();

    /// <summary>Cohere Embed v3 provider configuration (SPEC-20260927-voyage-and-cohere-embeddings).</summary>
    public CohereOptions Cohere { get; set; } = new();

    public sealed class AsymmetricOptions
    {
        /// <summary>Master switch — default off so existing corpora keep working.</summary>
        public bool Enabled { get; set; }

        /// <summary>Derive prefixes from the model name (nomic/e5/bge) when the
        /// explicit prefixes are unset.</summary>
        public bool Auto { get; set; } = true;

        /// <summary>Explicit query prefix — wins over auto-detection.</summary>
        public string? QueryPrefix { get; set; }

        /// <summary>Explicit document prefix — wins over auto-detection.</summary>
        public string? DocumentPrefix { get; set; }
    }

    /// <summary>Voyage AI embedding provider options (SPEC-20260927-voyage-and-cohere-embeddings RF-001).</summary>
    public sealed class VoyageOptions
    {
        /// <summary>Bearer key for Voyage AI. Never logged.</summary>
        public string? ApiKey { get; set; }

        /// <summary>Model name — voyage-3 (1024), voyage-3-large (1536), voyage-code-3 (1536).</summary>
        public string? Model { get; set; }

        /// <summary>Expected vector length; 0 falls back to the top-level Embeddings:Dimensions.</summary>
        public int Dimensions { get; set; }

        /// <summary>Maximum texts per request — Voyage allows up to 128.</summary>
        public int BatchSize { get; set; }

        /// <summary>Endpoint — defaults to the public Voyage AI API.</summary>
        public string? Endpoint { get; set; }
    }

    /// <summary>Cohere Embed v3 provider options (SPEC-20260927-voyage-and-cohere-embeddings RF-002).</summary>
    public sealed class CohereOptions
    {
        /// <summary>Bearer key for Cohere. Never logged.</summary>
        public string? ApiKey { get; set; }

        /// <summary>Model name — embed-multilingual-v3.0 (1024), embed-english-v3.0 (1024),
        /// embed-multilingual-light-v3.0 (384).</summary>
        public string? Model { get; set; }

        /// <summary>Expected vector length; 0 falls back to the top-level Embeddings:Dimensions.</summary>
        public int Dimensions { get; set; }

        /// <summary>Maximum texts per request — Cohere allows up to 96.</summary>
        public int BatchSize { get; set; }

        /// <summary>Endpoint — defaults to the public Cohere API v2.</summary>
        public string? Endpoint { get; set; }
    }
}
