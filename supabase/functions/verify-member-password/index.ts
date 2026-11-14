// POST { member_id, password } -> { proof } | { error }
// G12 (0062), login mode: a member at the desk with no phone and no desk PIN types their own account password on the desk screen.
// The desk's session is the caller; the member's password is checked with Supabase Auth in a separate client that keeps nothing,
// and the session that check creates is signed out at once, so nobody stays signed in as the member. The password is never
// stored, logged or sent anywhere else. The database counts wrong tries with the desk PIN's and hands out a one-use proof that
// record_check_in accepts with method 'password'.
import { createClient } from 'npm:@supabase/supabase-js@2';
import { adminClient, cors, reply } from '../_shared/admin.ts';

const codes = ['forbidden', 'not_found', 'pin_locked'];
const refusal = (message: string | undefined) => codes.find(c => message?.includes(c)) ?? 'bad_input';

Deno.serve(async (req) => {
  if (req.method === 'OPTIONS') return new Response('ok', { headers: cors });
  if (req.method !== 'POST') return reply({ error: 'bad_input' }, 405);

  const admin = adminClient();
  const token = req.headers.get('Authorization')?.replace(/^Bearer\s+/i, '');
  if (!token) return reply({ error: 'forbidden' }, 401);
  const { data: caller, error: callerError } = await admin.auth.getUser(token);
  if (callerError || !caller.user) return reply({ error: 'forbidden' }, 401);

  const body = await req.json().catch(() => ({}));
  const memberId = String(body.member_id ?? '');
  const password = String(body.password ?? '');
  if (!memberId || !password || password.length > 128) return reply({ error: 'bad_input' }, 400);

  // the desk may check people in, the account is a member's, and it isn't locked out; the answer is the sign-in email
  const { data: email, error: targetError } = await admin.rpc('desk_password_target', { p_desk: caller.user.id, p_member: memberId });
  if (targetError || !email) return reply({ error: refusal(targetError?.message) }, 403);

  const check = createClient(Deno.env.get('SUPABASE_URL')!, Deno.env.get('SUPABASE_ANON_KEY')!, {
    auth: { autoRefreshToken: false, persistSession: false },
  });
  const { data: signedIn, error: signInError } = await check.auth.signInWithPassword({ email, password });
  if (signedIn?.session) await admin.auth.admin.signOut(signedIn.session.access_token, 'local');
  // an Auth outage is not a wrong password: say so and count nothing
  if (signInError && !/invalid login credentials/i.test(signInError.message)) return reply({ error: 'auth_unavailable' }, 503);

  const { data: result, error: resultError } = await admin.rpc('desk_password_result',
    { p_desk: caller.user.id, p_member: memberId, p_ok: !signInError && !!signedIn?.session });
  if (resultError || !result) return reply({ error: refusal(resultError?.message) }, 403);
  return reply(result, result.proof ? 200 : 403);
});
