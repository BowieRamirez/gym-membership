// POST { payment_id, check? } -> { url } | { paid } | { error }
// Xendit plan (2026-11-16, X5, X8, X10): the member's "Pay online". Only the signed-in owner of a pending online (qr) payment that
// hasn't run out may use it. The payment keeps one Xendit Payment Session: still ACTIVE, its link is reused; COMPLETED, the payment is
// confirmed here (in case the webhook was lost); EXPIRED or CANCELED, a new session replaces it (0076), never past the payment's
// own deadline. With check: true nothing is created; the answer is only whether it is paid.
import { adminClient, cors, reply } from '../_shared/admin.ts';
import { confirmPaid, nextStep, paymentChannel, sessionExpiry, xendit } from '../_shared/xendit.ts';

// the designed "go back to the app" page (docs/pay-done, GitHub Pages; owner 2026-11-16): Supabase serves a function's HTML as plain text.
// xendit-webhook's GET still answers in plain text for sessions made before this change.
const RETURN_URL = 'https://bowieramirez.github.io/gym-membership/pay-done/';

Deno.serve(async (req) => {
  if (req.method === 'OPTIONS') return new Response('ok', { headers: cors });
  if (req.method !== 'POST') return reply({ error: 'bad_input' }, 405);

  const admin = adminClient();
  const token = req.headers.get('Authorization')?.replace(/^Bearer\s+/i, '');
  if (!token) return reply({ error: 'forbidden' }, 401);
  const { data: caller, error: callerError } = await admin.auth.getUser(token);
  if (callerError || !caller.user) return reply({ error: 'forbidden' }, 401);

  const body = await req.json().catch(() => ({}));
  const paymentId = Number(body.payment_id);
  if (!Number.isSafeInteger(paymentId) || paymentId <= 0) return reply({ error: 'bad_input' }, 400);
  const check = body.check === true;

  const load = () => admin.from('payments')
    .select('payment_id, member_id, recorded_by, status, method, amount, expires_at, payment_code, xendit_id, xendit_url')
    .eq('payment_id', paymentId).maybeSingle();
  const { data: p } = await load();
  // someone else's payment looks the same as no payment
  // the member's own payment, or one the desk recorded for a customer at the counter (cashier plan Y6)
  if (!p || (p.member_id !== caller.user.id && p.recorded_by !== caller.user.id)) return reply({ error: 'not_found' }, 404);
  if (p.status === 'verified') return reply({ paid: true });
  if (p.status !== 'pending' || p.method !== 'qr') return reply({ error: 'already_processed' }, 409);

  try {
    const session = p.xendit_id ? await xendit(`/sessions/${encodeURIComponent(p.xendit_id)}`) : null;
    const step = nextStep(session?.status ?? null);
    if (step === 'confirm') {
      // a missing amount is a mismatch in confirm_online_payment (0077), never a pass
      const amount = typeof session.amount === 'number' ? session.amount : null;
      const channel = session.channel_code ?? await paymentChannel(session.payment_request_id ?? session.latest_payment_request_id);
      const result = await confirmPaid(admin, p.xendit_id, amount, channel);
      return reply({ paid: result === 'confirmed' || result === 'already' });
    }
    if (check) return reply({ paid: false });
    if (step === 'reuse' && p.xendit_url) return reply({ url: p.xendit_url });
    if (step === 'wait') return reply({ error: 'unavailable' }, 409);   // Xendit is still settling the last session
    const end = sessionExpiry(Date.parse(p.expires_at), Date.now());
    if (end === null) return reply({ error: 'payment_expired' }, 409);

    const expires = new Date(end).toISOString();
    const created = await xendit('/sessions', {
      method: 'POST',
      body: JSON.stringify({
        reference_id: `${p.payment_code}-${Date.now()}`,   // unique per session; a payment may need several
        session_type: 'PAY', mode: 'PAYMENT_LINK', currency: 'PHP', country: 'PH', locale: 'en',
        amount: Number(p.amount), expires_at: expires,
        description: `Gym payment ${p.payment_code}`,
        success_return_url: RETURN_URL, cancel_return_url: RETURN_URL,
      }),
    });
    const { error } = await admin.rpc('save_online_checkout', {
      p_payment_id: p.payment_id, p_xendit_id: created.payment_session_id ?? created.id, p_url: created.payment_link_url,
      p_replaces: p.xendit_id,
    });
    if (error) throw new Error(error.message);
    // a second tap may have saved its session first: everyone gets the one that is stored
    const { data: now } = await load();
    return now?.xendit_url ? reply({ url: now.xendit_url }) : reply({ error: 'already_processed' }, 409);
  } catch (e) {
    console.log('xendit-checkout', paymentId, (e as Error).message);
    return reply({ error: 'unavailable' }, 502);
  }
});
