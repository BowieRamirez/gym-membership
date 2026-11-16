// Shared by xendit-checkout and xendit-webhook (Xendit plan, 2026-11-16). The secret key and webhook token live only in the Supabase
// secrets XENDIT_SECRET_KEY and XENDIT_WEBHOOK_TOKEN; they never ship in the app or in git.
import type { SupabaseClient } from 'npm:@supabase/supabase-js@2';

/** Calls the Xendit API with the secret key (HTTP Basic, empty password). Throws on any non-2xx answer. */
export async function xendit(path: string, init: RequestInit = {}): Promise<any> {
  const key = Deno.env.get('XENDIT_SECRET_KEY');
  if (!key) throw new Error('XENDIT_SECRET_KEY is not set');
  const res = await fetch(`https://api.xendit.co${path}`, {
    ...init,
    headers: { Authorization: `Basic ${btoa(`${key}:`)}`, 'Content-Type': 'application/json', ...(init.headers ?? {}) },
  });
  const body = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(`xendit ${res.status} ${body.error_code ?? ''} ${body.message ?? ''}`);
  return body;
}

/** What xendit-checkout does with the payment's stored session, by its Xendit status. Only a session that can no longer be paid
 *  (EXPIRED, CANCELED) is replaced; an unknown state is left alone, so a session that might still be paid is never dropped. */
export function nextStep(status: string | null): 'create' | 'reuse' | 'confirm' | 'wait' {
  if (status === null || status === 'EXPIRED' || status === 'CANCELED') return 'create';
  if (status === 'ACTIVE') return 'reuse';
  if (status === 'COMPLETED') return 'confirm';
  return 'wait';
}

/** A new session's end: 30 minutes (Xendit's default), but 5 minutes before the payment's own deadline, so a payment made at the
 *  last moment is confirmed before the payment expires. Null when that leaves under a minute: too late to start paying. */
export function sessionExpiry(paymentExpiresMs: number, nowMs: number): number | null {
  const end = Math.min(paymentExpiresMs - 5 * 60_000, nowMs + 30 * 60_000);
  return end - nowMs < 60_000 ? null : end;
}

/** How the customer paid (GCASH, PAYMAYA, QRPH, CARDS...), from the session's payment request: the session webhook doesn't say.
 *  Null when there is no id or the lookup fails; the method is only for the log and must never hold up a payment. */
export async function paymentChannel(paymentRequestId: unknown): Promise<string | null> {
  if (typeof paymentRequestId !== 'string' || !paymentRequestId) return null;
  try {
    const pr = await xendit(`/v3/payment_requests/${encodeURIComponent(paymentRequestId)}`, { headers: { 'api-version': '2024-11-11' } });
    return typeof pr.channel_code === 'string' ? pr.channel_code : null;
  } catch { return null; }
}

/** confirm_online_payment (0075): 'confirmed', 'already' or 'refund'. A database error is thrown, so the caller can answer 500. */
export async function confirmPaid(admin: SupabaseClient, sessionId: string, amount: number | null, channel: string | null): Promise<string> {
  const { data, error } = await admin.rpc('confirm_online_payment', { p_xendit_id: sessionId, p_amount: amount, p_channel: channel });
  if (error) throw new Error(error.message);
  return data as string;
}

/** Constant-time comparison of the webhook token: both sides are hashed first, so length and content leak nothing. */
export async function sameToken(given: string, expected: string): Promise<boolean> {
  if (!expected) return false;
  const hash = async (s: string) => new Uint8Array(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(s)));
  const [a, b] = await Promise.all([hash(given), hash(expected)]);
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a[i] ^ b[i];
  return diff === 0;
}
