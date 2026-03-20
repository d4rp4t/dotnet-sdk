using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NArk.Storage.EfCore.Entities;

public class VtxoEntity
{
    public string Script { get; set; } = "";
    public string TransactionId { get; set; } = "";
    public int TransactionOutputIndex { get; set; }
    public string? SpentByTransactionId { get; set; }
    public string? SettledByTransactionId { get; set; }
    public long Amount { get; set; }
    public DateTimeOffset SeenAt { get; set; }
    public bool Recoverable { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public uint? ExpiresAtHeight { get; set; }
    public bool Preconfirmed { get; set; }
    public bool Unrolled { get; set; }
    public string? CommitmentTxids { get; set; }
    public string? ArkTxid { get; set; }
    public string? AssetsJson { get; set; }

    [Column("Metadata", TypeName = "jsonb")]
    public string? MetadataJson { get; set; }

    [NotMapped]
    public Dictionary<string, string>? Metadata
    {
        get => MetadataJson is null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(MetadataJson);
        set => MetadataJson = value is null ? null : JsonSerializer.Serialize(value);
    }

    public virtual ICollection<ArkIntentVtxoEntity> IntentVtxos { get; set; } = null!;

    internal static void Configure(EntityTypeBuilder<VtxoEntity> builder, ArkStorageOptions options)
    {
        builder.ToTable(options.VtxosTable, options.Schema);
        builder.HasKey(e => new { e.TransactionId, e.TransactionOutputIndex });
        builder.Property(e => e.SpentByTransactionId).HasDefaultValue(null);
        builder.Property(e => e.SettledByTransactionId).HasDefaultValue(null);
        builder.Property(e => e.MetadataJson).HasDefaultValue(null);
    }
}
