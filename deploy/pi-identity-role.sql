-- Apply only after the Identity schema migration and an explicit authentication launch decision.
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE
    "AspNetRoles",
    "AspNetUsers",
    "AspNetRoleClaims",
    "AspNetUserClaims",
    "AspNetUserLogins",
    "AspNetUserRoles",
    "AspNetUserTokens",
    "AspNetUserPasskeys",
    "IdentityAccountActivity"
TO varplats_reader;

GRANT USAGE, SELECT ON SEQUENCE
    "AspNetRoleClaims_Id_seq",
    "AspNetUserClaims_Id_seq"
TO varplats_reader;
