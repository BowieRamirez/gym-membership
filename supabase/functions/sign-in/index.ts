// POST { email, password } -> { access_token, refresh_token } | { error, minutes? }   (0103, 0104)
// Owner fixes 2026-11-19 Q2 (0103): 10 wrong passwords for one account lock its sign-in for 5 minutes. The app signs in here, not
// straight to Supabase Auth: the lock is checked first, the password is tried with Auth, and the result is counted. The password
// is never stored or logged. Supabase's own per-IP limit still covers anyone calling Auth directly (the Password Verification hook
// that would close that too needs the Team plan).
import { createClient } from 'npm:@supabase/supabase-js@2';
import { adminClient, cors, reply } from '../_shared/admin.ts';

Deno.serve(async (req) => {
  if (req.method === 'OPTIONS') return new Response('ok', { headers: cors });
  if (req.method !== 'POST') return reply({ error: 'bad_input' }, 405);

  const body = await req.json().catch(() => ({}));
  const email = String(body.email ?? '').trim().toLowerCase();
  const password = String(body.password ?? '');
  if (!email || email.length > 254 || !password || password.length > 128) return reply({ error: 'bad_input' }, 400);

  const admin = adminClient();
  // 0104: the try is counted before Auth sees it, so tries sent together can't all slip past the lock
  const { data: wait, error: checkError } = await admin.rpc('sign_in_begin', { p_email: email });
  if (checkError) return reply({ error: 'auth_unavailable' }, 503);
  if (wait > 0) return reply({ error: 'account_locked', minutes: wait }, 429);

  const auth = createClient(Deno.env.get('SUPABASE_URL')!, Deno.env.get('SUPABASE_ANON_KEY')!, {
    auth: { autoRefreshToken: false, persistSession: false },
  });
  const { data, error } = await auth.auth.signInWithPassword({ email, password });
  // an Auth outage or an unconfirmed email is not a wrong password: say so (the try stays counted, 0104)
  if (error && !/invalid login credentials/i.test(error.message)) {
    return reply({ error: /not confirmed/i.test(error.message) ? 'email_not_confirmed' : 'auth_unavailable' }, error.status ?? 503);
  }
  const locked = await admin.rpc('sign_in_result', { p_email: email, p_ok: !error && !!data.session });
  if (error || !data.session) {
    return locked.data > 0 ? reply({ error: 'account_locked', minutes: locked.data }, 429) : reply({ error: 'invalid_credentials' }, 400);
  }
  return reply({ access_token: data.session.access_token, refresh_token: data.session.refresh_token });
});
