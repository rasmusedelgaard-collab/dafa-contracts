# Deploying the test site to monsterasp.net

This guide is written for a non-developer. It takes about 30 minutes the first time. After
that, a deployment is one click (or automatic when code is merged to `main`).

> ## WARNING - test data only
>
> The test site runs in **Dev sign-in mode**: anyone who knows the shared **access code** can
> pick any demo user, including an administrator. **There is no real authentication**, only
> that one shared code.
>
> - **Never upload a real contract** or any real supplier, price or personal data to this site.
> - Use only the built-in demo data and dummy files you make up yourself.
> - Do not share the address outside the small group testing the app, and choose a website
>   name that is not guessable (not "dafa-contracts").
> - The app asks every visitor for the shared access code before it shows the demo users
>   (step 4 explains how to choose and share it). The app refuses to start on the test site
>   without one. After 10 wrong codes from one address it stops accepting codes from that
>   address for 10 minutes.
> - **Stop the site when nobody is testing**: Control Panel > Websites > your site >
>   Overview > **Stop**. Press **Start** when you need it again.
>
> Real contracts only go into the production environment (Azure + Microsoft Entra sign-in +
> SharePoint), described in `docs/RUNBOOK.md`.

## What you need

- A monsterasp.net account (Free is enough for testing; turn on two-factor sign-in for the account).
- Admin access to the GitHub repository.

Good to know about the Free plan (from monsterasp.net's own plan comparison):

- Hosted in the EU. 5 GB disk, 256 MB memory, database up to 1 GB.
- The site goes to sleep after 3 hours without visitors, and the first visit afterwards is slow.
  While asleep, the app's hourly reminder job does not run. For testing, sign in as an admin
  demo user and use the "run jobs now" action in the app's settings instead.
- A Free site is archived after 6 months without use.
- The address is a free subdomain (for example `yoursite.runasp.net`). On Free, HTTPS is
  switched on by monsterasp.net support on request, after the site has been uploaded - open a
  support ticket and ask for it once the first deployment has succeeded.
- The website and the database must be on the same plan (both Free or both Premium).

## Step 1 - Create the website

1. Sign in to the monsterasp.net Control Panel.
2. Go to **Websites** and create a new website. Pick a name that is hard to guess.
3. Open the website and go to **Scripting**. In **ASP.NET / .NET**, keep the modern .NET option
   (not ".NET Framework 4.x"). Leave **Bitness** at **32-bit (x86)**. Save.
4. Note the website address shown on the **Overview** page (for example
   `http://yoursite.runasp.net`). You need it in step 4.

## Step 2 - Create the database

1. Go to **Databases** and create a new **MSSQL** database (same plan as the website).
2. Open the database, go to **Users**, and click **Connection string** next to the user.
3. Copy the string under **Local access** (not "Remote access" - the app runs on monsterasp.net
   itself). It looks roughly like
   `Server=...; Database=dbXXXX; User Id=dbXXXX; Password=...; Encrypt=True; TrustServerCertificate=True; MultipleActiveResultSets=True`.
   Keep it somewhere safe for step 4. Treat it like a password.

You do not need to create any tables. The app creates them and fills in demo data the first
time it starts.

## Step 3 - Switch on Web Deploy and copy the credentials

1. Go to **Websites** > your website > **Deploy**.
2. On the **Visual Studio access** card, switch **WebDeploy** to **Enabled**.
3. The card now shows four values: **Server**, **Site**, **Login** and **Password**. Keep the
   page open for step 4. (The **Test connection** button on the same card checks that they work.)

## Step 4 - Add the secrets in GitHub

1. Open the repository on github.com.
2. Go to **Settings** > **Secrets and variables** > **Actions**.
3. On the **Secrets** tab, click **New repository secret** and add these six, one at a time.
   The names must match exactly.

| Secret name | Value | Where it comes from |
|---|---|---|
| `MONSTERASP_WEBDEPLOY_URL` | `https://siteXXXX.siteasp.net:8172` | **Server** on the Visual Studio access card |
| `MONSTERASP_WEBDEPLOY_SITE` | `siteXXXX` | **Site** on the same card |
| `MONSTERASP_WEBDEPLOY_USERNAME` | `siteXXXX` | **Login** on the same card |
| `MONSTERASP_WEBDEPLOY_PASSWORD` | the Web Deploy password | **Password** on the same card |
| `MONSTERASP_DB_CONNECTION_STRING` | the whole **Local access** connection string | Step 2 |
| `MONSTERASP_DEV_ACCESS_CODE` | the shared access code for testers, at least 12 characters | You make it up; see below |

**Choosing and sharing the access code**

- Make it long rather than clever: four or five random words with hyphens (for example the
  pattern `word-word-word-word`), or 16 or more characters from a password manager. It must
  be at least 12 characters, otherwise the deployment is skipped and the app would not start.
  Do not reuse a password from anywhere else, and do not use "dafa", the site name or a date.
- Share it only with the people testing, through a channel you already trust for passwords
  (a password manager's sharing feature, or in person). Do not put it in the same e-mail or
  chat message as the site address, and never in a GitHub issue, a commit or a document in
  the repository.
- Testers type it once per browser session, before they pick a demo user.
- To change it (someone left the test group, or it may have leaked): update the secret in
  GitHub and run **Deploy test** again. Everyone who is signed in is signed out and has to
  enter the new code.

4. Switch to the **Variables** tab, click **New repository variable** and add:

| Variable name | Value | Purpose |
|---|---|---|
| `MONSTERASP_SITE_URL` | for example `http://yoursite.runasp.net` | Optional but recommended. Lets the deployment check that the site answers afterwards. |

If any of the six secrets is missing, or the access code is shorter than 12 characters, the
deployment does not fail - it stops early with a "Deploy skipped" message that says what to fix.

If you later change the Web Deploy password in the Control Panel, update
`MONSTERASP_WEBDEPLOY_PASSWORD` as well. Avoid a password containing a double quote (`"`).

## Step 5 - Run the deployment

Automatic: every time code is merged to `main` and the **CI** workflow passes, the
**Deploy test** workflow runs by itself.

Manual:

1. In the repository, open the **Actions** tab.
2. Choose **Deploy test** in the left-hand list.
3. Click **Run workflow**, keep the branch (normally `main`), and click the green **Run workflow** button.
   It deploys the latest successful CI build of that branch.
4. Wait for the green tick (2-4 minutes). Click the run to read the summary.

The **Deploy test** workflow only appears in the Actions tab once its file is on the `main` branch.

## Step 6 - Verify

1. Open `<your site address>/healthz` in a browser. You should see a short "Healthy" style
   answer. The very first start can take a minute while the database is created.
2. Open `<your site address>/build-info.json` to see which version (commit) is running.
3. Open `<your site address>/`. You should be asked for the access code, and after entering
   it get the screen where you pick a demo user.

## If something goes wrong

| What you see | What to do |
|---|---|
| "Deploy skipped" in the run summary | Add the secrets it lists (step 4), or make the access code at least 12 characters, and run again. |
| The site shows HTTP 500 right after a deployment that changed the data model | The database still has the old tables. Recreate the database (see "After an update that changes the database" below). |
| The site does not start and the log mentions `Auth:Mode` or `Auth:Dev:AccessCode` | The app only starts on the test site with `Auth:Mode=Dev` **and** an access code of at least 12 characters. Check the `MONSTERASP_DEV_ACCESS_CODE` secret and deploy again. |
| "Too many wrong access codes" | Wait 10 minutes. The limit is per network address, so colleagues behind the same office connection share it. |
| The deploy step fails with a sign-in / 401 error | WebDeploy is not enabled, or the password secret is wrong (step 3). |
| The deploy step fails with `ERROR_DESTINATION_NOT_REACHABLE ... (404)` | The secret `MONSTERASP_WEBDEPLOY_URL` is not the Web Deploy server. It must be the **Server** value on the website's **Deploy** card, in the form `https://siteXXXX.siteasp.net:8172` (not the site's public address). Correct the secret and run the deployment again. |
| The deploy step fails with `ERROR_INSUFFICIENT_ACCESS_TO_SITE_FOLDER` while deleting a `.dll` | On the very first deployment the host's placeholder app is running and locks its files. In the Control Panel **Stop** the site, re-run the deployment, then **Start** the site. |
| The deploy step times out | Web Deploy uses port 8172. Run it again; if it keeps failing, contact monsterasp.net support. |
| "No successful CI run found" | CI has not passed for that branch yet. Open the **CI** workflow and look at the failure. |
| The site shows HTTP 500 / "failed to start" | Control Panel > your website > **Logs**, read the latest Application Error. The most common cause is a wrong connection string (check that you copied **Local access**). The Overview page also shows a server-side screenshot with the detailed error. |
| HTTP 500 mentioning a missing framework / .NET version | The host may not have the .NET 10 runtime. Add a repository **variable** `PUBLISH_SELF_CONTAINED` = `true`, re-run **CI**, then deploy again (the app then brings its own runtime). |
| The site shows "Application Pool is disabled" or stays offline | Control Panel > your website > Overview > **Start**. |
| You are signed out after every deployment | Expected for now on this host; see "Known limitations" in `docs/RUNBOOK.md`. |

## After an update that changes the database

The app creates its tables only when the database is empty; it does not change existing
tables yet. When a new version changes the data model (the release notes in `docs/STATUS.md`
say so - "Backend phase 2" does), the test database must be emptied once:

1. Control Panel > **Databases**: delete the test database and create a new one with the same
   plan (or drop all tables with the SQL tool there).
2. If the connection string changed, update the `MONSTERASP_DB_CONNECTION_STRING` secret.
3. Run **Deploy test** again. The app recreates the tables and the demo data on first start.

All test data entered by hand is lost. That is expected on the test site.

## Removing the test site

When testing is over: delete the website and the database in the Control Panel, and delete the
six secrets in GitHub. Nothing on the test site needs to be kept.
