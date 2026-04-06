using Microsoft.Extensions.Logging;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Payments;
using NArk.Abstractions.VTXOs;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Models;

namespace NArk.Swaps.Services;

/// <summary>
/// Subscribes to protocol events (VTXOs, intents, swaps) and automatically
/// updates payment and payment request statuses.
/// Register as a singleton after all storage implementations.
/// </summary>
public class PaymentTrackingService
{
    private readonly IPaymentStorage _paymentStorage;
    private readonly IPaymentRequestStorage _paymentRequestStorage;
    private readonly ILogger<PaymentTrackingService> _logger;

    public PaymentTrackingService(
        IPaymentStorage paymentStorage,
        IPaymentRequestStorage paymentRequestStorage,
        IVtxoStorage vtxoStorage,
        IIntentStorage intentStorage,
        ISwapStorage swapStorage,
        ILogger<PaymentTrackingService> logger)
    {
        _paymentStorage = paymentStorage;
        _paymentRequestStorage = paymentRequestStorage;
        _logger = logger;

        vtxoStorage.VtxosChanged += OnVtxoChanged;
        intentStorage.IntentChanged += OnIntentChanged;
        swapStorage.SwapsChanged += OnSwapChanged;
    }

    /// <summary>
    /// When a VTXO arrives, check if it matches a pending payment request.
    /// </summary>
    private async void OnVtxoChanged(object? sender, ArkVtxo vtxo)
    {
        try
        {
            if (vtxo.IsSpent()) return;

            var request = await _paymentRequestStorage.GetPaymentRequestByScript(vtxo.Script);
            if (request is null) return;

            var newReceived = request.ReceivedAmount + vtxo.Amount;
            var (newStatus, overpayment) = ResolveRequestStatus(request, newReceived);

            await _paymentRequestStorage.UpdatePaymentRequestStatus(
                request.WalletId, request.RequestId, newStatus, newReceived, overpayment);

            _logger.LogInformation(
                "Payment request {RequestId} received {Amount} sats (total: {Total}), status: {Status}",
                request.RequestId, vtxo.Amount, newReceived, newStatus);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing VTXO {TxId}:{Index} for payment request",
                vtxo.TransactionId, vtxo.TransactionOutputIndex);
        }
    }

    /// <summary>
    /// Determines the new status and overpayment of a payment request based on received amount.
    /// Any-amount requests (Amount=null) are Paid immediately on first funds.
    /// Fixed-amount requests require exact or over — no underpayment tolerance.
    /// </summary>
    private static (ArkPaymentRequestStatus Status, ulong Overpayment) ResolveRequestStatus(
        ArkPaymentRequest request, ulong newReceived)
    {
        // Any-amount request: paid as soon as anything arrives
        if (request.Amount is null)
            return (ArkPaymentRequestStatus.Paid, 0);

        var target = request.Amount.Value;

        if (newReceived >= target)
            return (ArkPaymentRequestStatus.Paid, newReceived - target);

        return (ArkPaymentRequestStatus.PartiallyPaid, 0);
    }

    /// <summary>
    /// When an intent state changes, update linked outbound payments.
    /// </summary>
    private async void OnIntentChanged(object? sender, ArkIntent intent)
    {
        try
        {
            var payments = await _paymentStorage.GetPayments(
                intentTxIds: [intent.IntentTxId]);

            foreach (var payment in payments)
            {
                if (payment.Status != ArkPaymentStatus.Pending) continue;

                var newStatus = intent.State switch
                {
                    ArkIntentState.BatchSucceeded => ArkPaymentStatus.Completed,
                    ArkIntentState.BatchFailed => ArkPaymentStatus.Failed,
                    ArkIntentState.Cancelled => ArkPaymentStatus.Failed,
                    _ => ArkPaymentStatus.Pending
                };

                if (newStatus == ArkPaymentStatus.Pending) continue;

                var failReason = newStatus == ArkPaymentStatus.Failed
                    ? intent.CancellationReason ?? "Intent failed"
                    : null;

                await _paymentStorage.UpdatePaymentStatus(
                    payment.WalletId, payment.PaymentId, newStatus, failReason);

                _logger.LogInformation(
                    "Payment {PaymentId} updated to {Status} from intent {IntentTxId}",
                    payment.PaymentId, newStatus, intent.IntentTxId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing intent {IntentTxId} for payment tracking",
                intent.IntentTxId);
        }
    }

    /// <summary>
    /// When a swap state changes, update linked outbound payments.
    /// </summary>
    private async void OnSwapChanged(object? sender, ArkSwap swap)
    {
        try
        {
            var payments = await _paymentStorage.GetPayments(swapIds: [swap.SwapId]);

            foreach (var payment in payments)
            {
                if (payment.Status != ArkPaymentStatus.Pending) continue;

                var newStatus = swap.Status switch
                {
                    ArkSwapStatus.Settled => ArkPaymentStatus.Completed,
                    ArkSwapStatus.Failed => ArkPaymentStatus.Failed,
                    ArkSwapStatus.Refunded => ArkPaymentStatus.Failed,
                    _ => ArkPaymentStatus.Pending
                };

                if (newStatus == ArkPaymentStatus.Pending) continue;

                var failReason = newStatus == ArkPaymentStatus.Failed
                    ? swap.FailReason ?? $"Swap {swap.Status}"
                    : null;

                await _paymentStorage.UpdatePaymentStatus(
                    payment.WalletId, payment.PaymentId, newStatus, failReason);

                _logger.LogInformation(
                    "Payment {PaymentId} updated to {Status} from swap {SwapId}",
                    payment.PaymentId, newStatus, swap.SwapId);
            }

            // Also check if this swap fulfills a payment request (reverse submarine → Lightning receive)
            if (swap.Status == ArkSwapStatus.Settled &&
                swap.SwapType == ArkSwapType.ReverseSubmarine)
            {
                await HandleReverseSwapSettled(swap);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing swap {SwapId} for payment tracking", swap.SwapId);
        }
    }

    private async Task HandleReverseSwapSettled(ArkSwap swap)
    {
        var requests = await _paymentRequestStorage.GetPaymentRequests(
            walletIds: [swap.WalletId],
            statuses: [ArkPaymentRequestStatus.Pending, ArkPaymentRequestStatus.PartiallyPaid]);

        foreach (var request in requests)
        {
            if (request.SwapId != swap.SwapId) continue;

            var receivedAmount = request.ReceivedAmount + (ulong)swap.ExpectedAmount;
            var (newStatus, overpayment) = ResolveRequestStatus(request, receivedAmount);

            await _paymentRequestStorage.UpdatePaymentRequestStatus(
                request.WalletId, request.RequestId, newStatus, receivedAmount, overpayment);
            break;
        }
    }
}
