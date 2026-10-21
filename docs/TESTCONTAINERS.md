# Test containers

The Docker containers the live tests run against. Each live test is skipped unless its variable is set; see [ENVIRONMENT.md](ENVIRONMENT.md#test-suite) for the variables.

| Container | What it is | Image |
|---|---|---|
| `sql_server_dev` | SQL Server 2022 with AdventureWorks2022, for the SQL tools. | [microsoft/mssql-server](https://hub.docker.com/r/microsoft/mssql-server) (`mcr.microsoft.com/mssql/server:2022-latest`) |
| `oracle_free_dev` | Oracle Database 23ai Free (`FREEPDB1`), for the Oracle tools. | [gvenzl/oracle-free](https://hub.docker.com/r/gvenzl/oracle-free) (`23-slim-faststart`) |
| `mysql_dev` | MySQL 8.4, for the MySQL tools; also the container the Docker tools' live tests inspect. | [mysql](https://hub.docker.com/_/mysql) (`8.4`) |
| `mariadb_dev` | MariaDB 11, for the MySQL tools' MariaDB side. | [mariadb](https://hub.docker.com/_/mariadb) (`11`) |
| `postgres_dev` | PostgreSQL 17, for the PostgreSQL tools. | [postgres](https://hub.docker.com/_/postgres) (`17`) |
| `samba_ad_dev` | Samba 4.24.7 Active Directory domain controller (`LAB.NEON.TEST`), for the directory tools. | [diegogslomp/samba-ad-dc](https://hub.docker.com/r/diegogslomp/samba-ad-dc) (`4.24.7`) |
| `searxng-core` | SearXNG metasearch engine, for the web search tools. | [searxng/searxng](https://hub.docker.com/r/searxng/searxng) (`latest`) |
| `searxng-valkey` | Valkey, SearXNG's cache. | [valkey/valkey](https://hub.docker.com/r/valkey/valkey) (`9-alpine`) |
| `homeassistant` | Home Assistant, for the Home Assistant tools. | [home-assistant](https://github.com/home-assistant/core/pkgs/container/home-assistant) (`ghcr.io/home-assistant/home-assistant:stable`) |
| `kokoro-fastapi-cpu` | Kokoro text-to-speech behind an OpenAI-style HTTP API, for HTTP voice output. | [kokoro-fastapi-cpu](https://github.com/remsky/Kokoro-FastAPI/pkgs/container/kokoro-fastapi-cpu) (`ghcr.io/remsky/kokoro-fastapi-cpu:latest`) |

## `samba_ad_dev` on a Mac

The image is amd64 only; Docker Desktop on Apple Silicon runs it under emulation, healthy in about a minute. macOS's own Kerberos already holds port 88, so the KDC goes to 1088:

```sh
docker run -d --name samba_ad_dev --hostname DC1 --privileged \
  -e REALM=LAB.NEON.TEST -e DOMAIN=LAB -e ADMIN_PASS='<admin password>' -e DNS_FORWARDER=1.1.1.1 -e BIND_NETWORK_INTERFACES=false \
  -p 127.0.0.1:389:389 -p 127.0.0.1:636:636 -p 127.0.0.1:1088:88 -p 127.0.0.1:1088:88/udp \
  -v samba_ad_dev_etc:/usr/local/samba/etc -v samba_ad_dev_private:/usr/local/samba/private -v samba_ad_dev_var:/usr/local/samba/var \
  diegogslomp/samba-ad-dc:4.24.7
docker exec samba_ad_dev samba-tool user create reader '<reader password>'
```

The live directory tests read a seed of their own (any password will do for the seeded accounts; nobody signs in as them):

```sh
docker exec samba_ad_dev sh -c '
B="DC=lab,DC=neon,DC=test"; PW="<a password>"
samba-tool ou create "OU=Neon,$B"; samba-tool ou create "OU=Engineering,OU=Neon,$B"; samba-tool ou create "OU=Sales,OU=Neon,$B"
for u in "alice Alice Smith" "bob Bob Jones" "carol Carol King" "dave Dave Lee" "grace Grace Hu"; do set -- $u; samba-tool user create $1 "$PW" --given-name=$2 --surname=$3; done
samba-tool user create erin "$PW" --given-name=Erin --surname=Ng --department=Engineering --userou="OU=Engineering,OU=Neon"
samba-tool user create frank "$PW" --given-name=Frank --surname=Ode --department=Sales --userou="OU=Sales,OU=Neon"
samba-tool user setexpiry erin --noexpiry; samba-tool user setexpiry frank --days=10
for g in Engineers Staff Tier0 Unused; do samba-tool group create $g; done
samba-tool group create All-Hands --group-type=Distribution --description="Company-wide mail list"
samba-tool group addmembers Engineers bob; samba-tool group addmembers Staff Engineers,carol
samba-tool group addmembers Tier0 grace; samba-tool group addmembers "Domain Admins" Tier0
samba-tool contact create "Vendor Rep"; samba-tool group addmembers All-Hands "Vendor Rep" --object-types=contact
samba-tool computer create WS01; samba-tool user disable --filter="(sAMAccountName=WS01\$)"
samba-tool domain passwordsettings set --account-lockout-threshold=10
samba-tool domain passwordsettings pso create ShortPasswords 10 --min-pwd-length=4
printf "dn: CN=Frank Ode,OU=Sales,OU=Neon,$B\nchangetype: modify\nreplace: manager\nmanager: CN=Erin Ng,OU=Engineering,OU=Neon,$B\n" | ldbmodify -H /usr/local/samba/private/sam.ldb'
```

`frank`'s account ends 10 days after seeding, so `account_expiring` finds him only within that time; run `samba-tool user setexpiry frank --days=10` again after.

Then, in the shell that runs the tests or the app:
* `KRB5_CONFIG` names a `krb5.conf` holding `[realms] LAB.NEON.TEST = { kdc = tcp/127.0.0.1:1088 }`.
* `/etc/hosts` holds `127.0.0.1 dc1.lab.neon.test`. A Mac signs in as `ldap/<server>`, and the DC answers to its own name only: `localhost` fails.
* `NEONSIDEKICK_TEST_AD_DOMAIN=Domain=lab.neon.test;Server=dc1.lab.neon.test;User=reader@LAB.NEON.TEST;Password=…` ([ENVIRONMENT.md](ENVIRONMENT.md#test-suite)).
* The refused-password test can't prove the hold here. The app asks port 88 of the DC's address, and on 127.0.0.1 that is macOS's own KDC, which knows no domain account. The test then checks that nothing is held.
