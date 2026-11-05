// POST { email } -> { in_use: boolean }
// The register page's live "email already in use" check. It is public on purpose (verify_jwt = false in config.toml): the person
// has no account yet. The limits are in check_email_in_use (0039), and the app treats any refusal as "nothing to show".
import { adminClient, cors, reply } from '../_shared/admin.ts';

Deno.serve(async (req) => {
  if (req.method === 'OPTIONS') return new Response('ok', { headers: cors });
  if (req.method !== 'POST') return reply({ error: 'bad_input' }, 405);

  const body = await req.json().catch(() => ({}));
  const email = String(body.email ?? '').trim();
  if (email.length > 100 || !/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(email)) return reply({ error: 'bad_input' }, 400);

  const ip = req.headers.get('cf-connecting-ip') ?? req.headers.get('x-forwarded-for')?.split(',')[0]?.trim() ?? 'unknown';
  const { data, error } = await adminClient().rpc('check_email_in_use', { p_ip: ip, p_email: email });
  if (error) return error.message === 'rate_limited' ? reply({ error: 'rate_limited' }, 429) : reply({ error: 'unavailable' }, 500);
  return reply({ in_use: data === true });
});
