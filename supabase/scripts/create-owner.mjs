// Creates the owner (admin) account on your Supabase project. Run it in your own terminal:
//   node supabase/scripts/create-owner.mjs
// It asks for your name, email and a password, so the password never goes through chat or into a file.
// It reads the project's service-role key through the Supabase CLI you are already logged in with, uses it once to create
// the account, and never writes it anywhere. Run `npx supabase link` first.
import { execSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import readline from 'node:readline';

const ref = readFileSync(new URL('../.temp/project-ref', import.meta.url), 'utf8').trim();
const url = `https://${ref}.supabase.co`;

const rl = readline.createInterface({ input: process.stdin, output: process.stdout });
const ask = (q) => new Promise((res) => rl.question(q, res));

// a password prompt that does not echo what is typed
function askHidden(q) {
  return new Promise((res) => {
    process.stdout.write(q);
    const stdin = process.stdin;
    let value = '';
    stdin.setRawMode?.(true);
    stdin.resume();
    const onData = (chunk) => {
      for (const ch of chunk.toString('utf8')) {
        if (ch === '\r' || ch === '\n') { stdin.setRawMode?.(false); stdin.off('data', onData); process.stdout.write('\n'); return res(value); }
        if (ch === '\u0003') process.exit(1);
        if (ch === '\u007f' || ch === '\b') value = value.slice(0, -1); else value += ch;
      }
    };
    stdin.on('data', onData);
  });
}

const name = (await ask('Your full name: ')).trim();
const email = (await ask('Your email: ')).trim().toLowerCase();
rl.close();
const password = await askHidden('Choose a password (at least 8 characters, typing is hidden): ');
const again = await askHidden('Type it again: ');
if (!name || !email.includes('@')) { console.error('Enter a name and a valid email.'); process.exit(1); }
if (password.length < 8 || password !== again) { console.error('The passwords must match and be at least 8 characters.'); process.exit(1); }

const keys = JSON.parse(execSync(`npx supabase projects api-keys --project-ref ${ref} -o json`, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] }).replace(/^[^[{]*/, ''));
const serviceKey = keys.find((k) => (k.name ?? k.id) === 'service_role')?.api_key;
if (!serviceKey) { console.error('Could not read the project keys. Run `npx supabase login` and `npx supabase link` first.'); process.exit(1); }

const res = await fetch(`${url}/auth/v1/admin/users`, {
  method: 'POST',
  headers: { apikey: serviceKey, Authorization: `Bearer ${serviceKey}`, 'Content-Type': 'application/json' },
  body: JSON.stringify({
    email, password, email_confirm: true,
    user_metadata: { username: email, full_name: name },
    app_metadata: { role: 'admin' },   // only the service role can set this; the sign-up trigger turns it into the admin role
  }),
});
const body = await res.json();
if (!res.ok) { console.error(`Could not create the account: ${body.msg ?? body.message ?? res.status}`); process.exit(1); }
console.log(`Done. ${email} is the owner of ${url}. Sign in to the app with it.`);
process.exit(0);
