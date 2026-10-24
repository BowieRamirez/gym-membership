// POST { first_name, last_name, email, role: 'member' | 'coach' | 'employee', password } (or { name, ... } from an older app)
// The owner creates an account with a temporary password (plan 4.3). The handle_new_user trigger
// builds the profile, settings, role and member/coach row from the metadata set here.
import { adminClient, cors, reply, requireAdmin } from '../_shared/admin.ts';

const ROLES = ['member', 'coach', 'employee'];

Deno.serve(async (req) => {
  if (req.method === 'OPTIONS') return new Response('ok', { headers: cors });
  if (req.method !== 'POST') return reply({ error: 'bad_input' }, 405);

  const admin = adminClient();
  const caller = await requireAdmin(req, admin);
  if (caller instanceof Response) return caller;

  const body = await req.json().catch(() => ({}));
  const first = String(body.first_name ?? '').trim();
  const last = String(body.last_name ?? '').trim();
  const name = first && last ? `${first} ${last}` : String(body.name ?? '').trim();
  const email = String(body.email ?? '').trim().toLowerCase();
  const role = String(body.role ?? '');
  const password = String(body.password ?? '');
  if (!name || !email.includes('@') || !ROLES.includes(role) || password.length < 6) return reply({ error: 'bad_input' }, 400);
  if (!caller.superAdmin && role === 'employee') return reply({ error: 'forbidden' }, 403);   // sub-admins add members and coaches only

  const { data, error } = await admin.auth.admin.createUser({
    email,
    password,
    email_confirm: true,
    user_metadata: { username: email, full_name: name, ...(first && last ? { first_name: first, last_name: last } : {}), must_change_password: true },
    app_metadata: { role },
  });
  if (error || !data.user) {
    const taken = /already|registered|exists/i.test(error?.message ?? '');
    return reply({ error: taken ? 'email_taken' : 'bad_input', detail: error?.message }, taken ? 409 : 400);
  }

  await admin.from('audit_log').insert({
    actor_id: caller.id, action: 'user.create', entity: 'profiles', entity_id: data.user.id, details: { role },
  });
  return reply({ id: data.user.id });
});
