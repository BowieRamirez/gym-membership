// POST { user_id, password }
// The owner sets a new temporary password for someone who forgot theirs (plan 4.4).
import { adminClient, cors, reply, requireAdmin } from '../_shared/admin.ts';

Deno.serve(async (req) => {
  if (req.method === 'OPTIONS') return new Response('ok', { headers: cors });
  if (req.method !== 'POST') return reply({ error: 'bad_input' }, 405);

  const admin = adminClient();
  const caller = await requireAdmin(req, admin);
  if (caller instanceof Response) return caller;

  const body = await req.json().catch(() => ({}));
  const userId = String(body.user_id ?? '');
  const password = String(body.password ?? '');
  if (!userId || password.length < 6) return reply({ error: 'bad_input' }, 400);

  const { error } = await admin.auth.admin.updateUserById(userId, { password });
  if (error) return reply({ error: /not found/i.test(error.message) ? 'not_found' : 'bad_input', detail: error.message }, 400);

  await admin.from('profiles').update({ must_change_password: true }).eq('id', userId);
  await admin.from('user_notifications').insert({
    user_id: userId, type: 'system', title: 'Your password was reset. Sign in with the new one.',
  });
  await admin.from('audit_log').insert({ actor_id: caller.id, action: 'user.reset_password', entity: 'profiles', entity_id: userId });
  return reply({ ok: true });
});
