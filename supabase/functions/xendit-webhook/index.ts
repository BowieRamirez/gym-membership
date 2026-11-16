// Xendit plan (2026-11-16, X5, X9): Xendit calls this when a Payment Session changes. Public (verify_jwt = false in config.toml);
// only a request carrying the webhook verification token (x-callback-token) is believed. A completed session confirms its payment
// through confirm_online_payment (0075), which is idempotent, so Xendit's retries are harmless. Everything else is acknowledged and
// ignored: expiry is handled by expire_payments. A database error answers 500 so Xendit retries.
// GET is the checkout's return page: plain text telling the member to go back to the app (X12).
import { adminClient, reply } from '../_shared/admin.ts';
import { confirmPaid, sameToken } from '../_shared/xendit.ts';

Deno.serve(async (req) => {
  if (req.method === 'GET')
    return new Response('Payment done. You can close this page and go back to the gym app.', { headers: { 'Content-Type': 'text/plain; charset=utf-8' } });
  if (req.method !== 'POST') return reply({ error: 'bad_input' }, 405);

  if (!await sameToken(req.headers.get('x-callback-token') ?? '', Deno.env.get('XENDIT_WEBHOOK_TOKEN') ?? ''))
    return reply({ error: 'forbidden' }, 401);

  const body = await req.json().catch(() => ({}));
  if (body.event !== 'payment_session.completed') return reply({ ok: true });
  const d = body.data ?? {};
  if (typeof d.payment_session_id !== 'string' || typeof d.amount !== 'number') return reply({ ok: true });

  try {
    const result = await confirmPaid(adminClient(), d.payment_session_id, d.amount, d.channel_code ?? null);
    console.log('xendit-webhook', d.payment_session_id, result);
    return reply({ ok: true });
  } catch (e) {
    const message = (e as Error).message;
    if (message.includes('not_found')) { console.log('xendit-webhook unknown session', d.payment_session_id); return reply({ ok: true }); }
    console.log('xendit-webhook failed', d.payment_session_id, message);
    return reply({ error: 'unavailable' }, 500);
  }
});
