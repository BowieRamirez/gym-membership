// POST { payment_id, check? } -> { url } | { paid } | { error }
// Xendit plan (2026-11-16, X5, X8, X10): the member's "Pay online". Only the signed-in owner of a pending online (qr) payment that
// hasn't run out may use it. The payment keeps one Xendit Payment Session: still ACTIVE, its link is reused; COMPLETED, the payment is
// confirmed here (in case the webhook was lost); EXPIRED or CANCELED, a new session replaces it (0076), never past the payment's
// own deadline. With check: true nothing is created; the answer is only whether it is paid.
import { adminClient, cors, reply } from '../_shared/admin.ts';
import { confirmPaid, xendit } from '../_shared/xendit.ts';

const RETURN_URL = `${Deno.env.get('SUPABASE_URL')}/functions/v1/xendit-webhook`;   // its GET says "go back to the app"
const SESSION_MINUTES = 30;   // Xendit's default; sessions are meant to be short-lived

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
    .select('payment_id, member_id, status, method, amount, expires_at, payment_code, xendit_id, xendit_url')
    .eq('payment_id', paymentId).maybeSingle();
  const { data: p } = await load();
  // someone else's payment looks the same as no payment
  if (!p || p.member_id !== caller.user.id) return reply({ error: 'not_found' }, 404);
  if (p.status === 'verified') return reply({ paid: true });
  if (p.status !== 'pending' || p.method !== 'qr') return reply({ error: 'already_processed' }, 409);

  try {
    const session = p.xendit_id ? await xendit(`/sessions/${encodeURIComponent(p.xendit_id)}`) : null;
    if (session?.status === 'COMPLETED') {
      const result = await confirmPaid(admin, p.xendit_id, Number(session.amount), session.channel_code ?? null);
      return reply({ paid: result === 'confirmed' || result === 'already' });
    }
    if (check) return reply({ paid: false });
    if (Date.parse(p.expires_at) <= Date.now()) return reply({ error: 'payment_expired' }, 409);
    if (session?.status === 'ACTIVE' && p.xendit_url) return reply({ url: p.xendit_url });

    const expires = new Date(Math.min(Date.parse(p.expires_at), Date.now() + SESSION_MINUTES * 60_000)).toISOString();
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
