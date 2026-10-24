// Shared by the create-user and reset-password Edge Functions.
// The service-role key lives only here, in the Supabase runtime. It never ships in the app.
import { createClient, type SupabaseClient } from 'npm:@supabase/supabase-js@2';

export const cors = {
  'Access-Control-Allow-Origin': '*',
  'Access-Control-Allow-Headers': 'authorization, x-client-info, apikey, content-type',
};

export function reply(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { ...cors, 'Content-Type': 'application/json' } });
}

export function adminClient(): SupabaseClient {
  return createClient(Deno.env.get('SUPABASE_URL')!, Deno.env.get('SUPABASE_SERVICE_ROLE_KEY')!, {
    auth: { autoRefreshToken: false, persistSession: false },
  });
}

/** Staff roles a sub-admin may not act on (0036): only the super admin manages these accounts. */
export const STAFF_ROLES = ['admin', 'subadmin', 'employee'];

/**
 * Returns the caller's id if they are a signed-in, active super admin (role admin) or sub-admin, and which one; otherwise a ready-made
 * error response. A sub-admin may only act on members and coaches; each function checks that.
 */
export async function requireAdmin(req: Request, admin: SupabaseClient): Promise<{ id: string; superAdmin: boolean } | Response> {
  const token = req.headers.get('Authorization')?.replace(/^Bearer\s+/i, '');
  if (!token) return reply({ error: 'forbidden' }, 401);

  const { data, error } = await admin.auth.getUser(token);
  if (error || !data.user) return reply({ error: 'forbidden' }, 401);

  const [{ data: profile }, { data: roles }] = await Promise.all([
    admin.from('profiles').select('is_active').eq('id', data.user.id).maybeSingle(),
    admin.from('user_roles').select('roles!inner(name)').eq('user_id', data.user.id).in('roles.name', ['admin', 'subadmin']),
  ]);
  if (!profile?.is_active || !roles?.length) return reply({ error: 'forbidden' }, 403);

  return { id: data.user.id, superAdmin: roles.some((r: { roles: { name: string } }) => r.roles.name === 'admin') };
}
