import type { Configuration } from '@azure/msal-browser'

export const msalConfig: Configuration = {
  auth: {
    clientId: import.meta.env.VITE_AZURE_AD_CLIENT_ID,
    authority: `https://login.microsoftonline.com/${import.meta.env.VITE_AZURE_AD_TENANT_ID}`,
    redirectUri: import.meta.env.VITE_AZURE_AD_REDIRECT_URI,
  },
  cache: {
    cacheLocation: 'sessionStorage',
  },
}

export const apiScopes = [import.meta.env.VITE_AZURE_AD_API_SCOPE]
