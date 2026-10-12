// Creates the four demo logins through Supabase Auth, so the sign-up trigger builds their rows.
//   SUPABASE_URL=https://<ref>.supabase.co SUPABASE_SERVICE_ROLE_KEY=<key> node supabase/scripts/seed-demo-users.mjs
// Dev projects only. The service-role key stays in your terminal; never put it in the app or in git.

const url = process.env.SUPABASE_URL;
const key = process.env.SUPABASE_SERVICE_ROLE_KEY;
if (!url || !key) { console.error('Set SUPABASE_URL and SUPABASE_SERVICE_ROLE_KEY.'); process.exit(1); }

const users = [
  { email: 'admin@demo.io',    name: 'Owner',        role: 'admin' },
  { email: 'employee@demo.io', name: 'Front Desk',   role: 'employee' },
  { email: 'coach@demo.io',    name: 'Coach Kuts',   role: 'coach' },
  { email: 'member@demo.io',   name: 'Alex Member',  role: 'member' },
];

for (const u of users) {
  const res = await fetch(`${url}/auth/v1/admin/users`, {
    method: 'POST',
    headers: { apikey: key, Authorization: `Bearer ${key}`, 'Content-Type': 'application/json' },
    body: JSON.stringify({
      email: u.email, password: 'demo1234', email_confirm: true,
      user_metadata: { username: u.email, full_name: u.name },
      app_metadata: { role: u.role },
    }),
  });
  const body = await res.json();
  console.log(res.ok ? 'created' : `skipped (${body.msg ?? body.message ?? res.status})`, u.email);
}
