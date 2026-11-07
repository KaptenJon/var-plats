function antiforgeryToken() {
    return document.querySelector('input[name="__RequestVerificationToken"]')?.value;
}

function decodeBase64Url(value) {
    const bytes = Uint8Array.from(atob(value.replace(/-/g, '+').replace(/_/g, '/')), char => char.charCodeAt(0));
    return bytes.buffer;
}

function requestOptions(options) {
    if (PublicKeyCredential.parseRequestOptionsFromJSON) {
        return PublicKeyCredential.parseRequestOptionsFromJSON(options);
    }
    return {
        ...options,
        challenge: decodeBase64Url(options.challenge),
        allowCredentials: options.allowCredentials?.map(credential => ({
            ...credential,
            id: decodeBase64Url(credential.id)
        }))
    };
}

function creationOptions(options) {
    if (PublicKeyCredential.parseCreationOptionsFromJSON) {
        return PublicKeyCredential.parseCreationOptionsFromJSON(options);
    }
    return {
        ...options,
        challenge: decodeBase64Url(options.challenge),
        user: { ...options.user, id: decodeBase64Url(options.user.id) },
        excludeCredentials: options.excludeCredentials?.map(credential => ({
            ...credential,
            id: decodeBase64Url(credential.id)
        }))
    };
}

async function postJson(url, body) {
    const token = antiforgeryToken();
    if (!token) throw new Error('Sidan har gått ut. Ladda om och försök igen.');
    const response = await fetch(url, {
        method: 'POST',
        credentials: 'same-origin',
        headers: {
            'Content-Type': 'application/json',
            'RequestVerificationToken': token
        },
        body: JSON.stringify(body)
    });
    if (!response.ok) throw new Error('Inloggningen kunde inte slutföras. Kontrollera uppgifterna och försök igen.');
    return response;
}

async function obtainCredential(optionsUrl, body, credentialUrl, transform) {
    const optionsResponse = await postJson(optionsUrl, body);
    const options = transform(await optionsResponse.json());
    const credential = await navigator.credentials[transform === requestOptions ? 'get' : 'create']({
        publicKey: options
    });
    if (!credential) throw new Error('Ingen säkerhetsnyckel valdes.');
    const credentialJson = JSON.stringify(credential.toJSON ? credential.toJSON() : credential);
    return postJson(credentialUrl, { credentialJson });
}

export async function signIn() {
    try {
        if (!window.PublicKeyCredential || !navigator.credentials) {
            throw new Error('Webbläsaren stöder inte passkeys eller FIDO2-nycklar.');
        }
        await obtainCredential(
            '/auth/passkeys/request-options',
            {},
            '/auth/passkeys/sign-in',
            requestOptions);
        window.location.assign('/');
        return '';
    } catch (error) {
        if (error.name === 'NotAllowedError') return 'Ingen nyckel användes.';
        return error.message || 'Inloggningen misslyckades.';
    }
}

export async function addPasskey() {
    try {
        if (!window.PublicKeyCredential || !navigator.credentials) {
            throw new Error('Webbläsaren stöder inte passkeys eller FIDO2-nycklar.');
        }
        await obtainCredential(
            '/auth/passkeys/creation-options',
            {},
            '/auth/passkeys/register',
            creationOptions);
        window.location.reload();
        return '';
    } catch (error) {
        if (error.name === 'NotAllowedError') return 'Ingen nyckel registrerades.';
        return error.message || 'Nyckeln kunde inte registreras.';
    }
}

export async function removePasskey(credentialId) {
    const token = antiforgeryToken();
    if (!token) return 'Sidan har gått ut. Ladda om och försök igen.';
    const response = await fetch(`/auth/passkeys/remove/${encodeURIComponent(credentialId)}`, {
        method: 'POST',
        credentials: 'same-origin',
        headers: { 'RequestVerificationToken': token }
    });
    if (!response.ok) return 'Nyckeln kunde inte tas bort. Se till att du har en annan inloggningsmetod.';
    window.location.reload();
    return '';
}
