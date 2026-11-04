# Keycloak composition integration tests

These optional acceptance tests add local/remote composition to the public Keycloak
fixtures. They require Docker with Linux containers and the explicitly selected
Keycloak image. Run `run-typed-tests.ps1` from this directory after following
`tests/InterfaceDB.Authentication.KeycloakTests/README.md` at the repository root.
The regular build compiles these tests; the standard CI suite does not start Keycloak.
