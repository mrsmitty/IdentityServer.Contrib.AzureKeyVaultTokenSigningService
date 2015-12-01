﻿using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using IdentityServer3.Core;
using IdentityServer3.Core.Models;
using IdentityServer3.Core.Services;
using Microsoft.Azure.KeyVault;
using Microsoft.Azure.KeyVault.WebKey;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using Newtonsoft.Json.Linq;
using AuthenticationContext = Microsoft.IdentityModel.Clients.ActiveDirectory.AuthenticationContext;

namespace IdentityServer3.Contrib.AzureKeyVaultTokenSigningService
{
    // This project can output the Class library as a NuGet Package.
    // To enable this option, right-click on the project and select the Properties menu item. In the Build tab select "Produce outputs on build".
    public class AzureKeyVaultTokenSigningService : ITokenSigningService
    {
        private readonly string _keyIdentifier;
        private readonly string _keyVaultClientId;
        private readonly string _keyVaultClientSecret;
        private byte[] _keyVaultKeyExponent;
        private byte[] _keyVaultKeyModulus;

        /// <summary>
        /// Initializes a new instance of the <see cref="AzureKeyVaultTokenSigningService"/> class.
        /// </summary>
        /// <param name="options">The options.</param>
        /// <param name="keyIdentifier"></param>
        /// <param name="keyVaultClientId"></param>
        /// <param name="keyVaultClientSecret"></param>
        public AzureKeyVaultTokenSigningService(string keyIdentifier, string keyVaultClientId, string keyVaultClientSecret)
        {
            _keyIdentifier = keyIdentifier;
            _keyVaultClientId = keyVaultClientId;
            _keyVaultClientSecret = keyVaultClientSecret;
        }

        /// <summary>
        /// Signs the token.
        /// </summary>
        /// <param name="token">The token.</param>
        /// <returns>
        /// A protected and serialized security token
        /// </returns>
        public virtual async Task<string> SignTokenAsync(Token token)
        {
            var credentials = await GetSigningCredentialsAsync();
            return await CreateJsonWebToken(token, credentials);
        }

        /// <summary>
        /// Retrieves the signing credential (override to load key from alternative locations)
        /// </summary>
        /// <returns>The signing credential</returns>
        protected virtual async Task<AzureKeyVaultSigningCredentials> GetSigningCredentialsAsync()
        {
            if (_keyVaultKeyExponent == null && _keyVaultKeyModulus == null)
            {
                var keyVaultClient = new KeyVaultClient(KeyVaultClientAuthenticationCallback);
                var keyBundle = await keyVaultClient.GetKeyAsync(_keyIdentifier).ConfigureAwait(false);
                
                _keyVaultKeyExponent = keyBundle.Key.E;
                _keyVaultKeyModulus = keyBundle.Key.N;
            }

            var rsa = RSA.Create();
            rsa.ImportParameters(new RSAParameters
            {
                Exponent = _keyVaultKeyExponent,
                Modulus = _keyVaultKeyModulus,
            });

            var securityKey = new RsaSecurityKey(rsa);
            return new AzureKeyVaultSigningCredentials(securityKey, SecurityAlgorithms.Sha256Digest);
        }

        /// <summary>
        /// Creates the json web token.
        /// </summary>
        /// <param name="token">The token.</param>
        /// <param name="credentials">The credentials.</param>
        /// <returns>The signed JWT</returns>
        protected virtual async Task<string> CreateJsonWebToken(Token token, AzureKeyVaultSigningCredentials credentials)
        {
            var header = CreateHeader(token, credentials);
            var payload = CreatePayload(token);

            return await SignAsync(new JwtSecurityToken(header, payload));
        }

        /// <summary>
        /// Creates the JWT header
        /// </summary>
        /// <param name="token">The token.</param>
        /// <param name="keyVaultCredentials">The credentials.</param>
        /// <returns>The JWT header</returns>
        protected virtual JwtHeader CreateHeader(Token token, AzureKeyVaultSigningCredentials keyVaultCredentials)
        {
            var header = new JwtHeader(keyVaultCredentials);
            if (keyVaultCredentials != null)
            {
                header.Add("kid", _keyIdentifier);
            }

            return header;
        }

        /// <summary>
        /// Creates the JWT payload
        /// </summary>
        /// <param name="token">The token.</param>
        /// <returns>The JWT payload</returns>
        protected virtual JwtPayload CreatePayload(Token token)
        {
            var payload = new JwtPayload(
                token.Issuer,
                token.Audience,
                null,
                DateTime.UtcNow,
                DateTime.UtcNow.AddSeconds(token.Lifetime));

            var amrClaims = token.Claims.Where(x => x.Type == Constants.ClaimTypes.AuthenticationMethod);
            var jsonClaims = token.Claims.Where(x => x.ValueType == Constants.ClaimValueTypes.Json);
            var normalClaims = token.Claims.Except(amrClaims).Except(jsonClaims);

            payload.AddClaims(normalClaims);

            // deal with amr
            var amrValues = amrClaims.Select(x => x.Value).Distinct().ToArray();
            if (amrValues.Any())
            {
                payload.Add(Constants.ClaimTypes.AuthenticationMethod, amrValues);
            }

            // deal with json types
            // calling ToArray() to trigger JSON parsing once and so later 
            // collection identity comparisons work for the anonymous type
            var jsonTokens = jsonClaims.Select(x => new { x.Type, JsonValue = JRaw.Parse(x.Value) }).ToArray();

            var jsonObjects = jsonTokens.Where(x => x.JsonValue.Type == JTokenType.Object).ToArray();
            var jsonObjectGroups = jsonObjects.GroupBy(x => x.Type).ToArray();
            foreach (var group in jsonObjectGroups)
            {
                if (payload.ContainsKey(group.Key))
                {
                    throw new Exception(String.Format("Can't add two claims where one is a JSON object and the other is not a JSON object ({0})", group.Key));
                }

                if (group.Skip(1).Any())
                {
                    // add as array
                    payload.Add(group.Key, group.Select(x => x.JsonValue).ToArray());
                }
                else
                {
                    // add just one
                    payload.Add(group.Key, group.First().JsonValue);
                }
            }

            var jsonArrays = jsonTokens.Where(x => x.JsonValue.Type == JTokenType.Array).ToArray();
            var jsonArrayGroups = jsonArrays.GroupBy(x => x.Type).ToArray();
            foreach (var group in jsonArrayGroups)
            {
                if (payload.ContainsKey(group.Key))
                {
                    throw new Exception(String.Format("Can't add two claims where one is a JSON array and the other is not a JSON array ({0})", group.Key));
                }

                List<JToken> newArr = new List<JToken>();
                foreach (var arrays in group)
                {
                    var arr = (JArray)arrays.JsonValue;
                    newArr.AddRange(arr);
                }

                // add just one array for the group/key/claim type
                payload.Add(group.Key, newArr.ToArray());
            }

            var unsupportedJsonTokens = jsonTokens.Except(jsonObjects).Except(jsonArrays);
            var unsupportedJsonClaimTypes = unsupportedJsonTokens.Select(x => x.Type).Distinct();
            if (unsupportedJsonClaimTypes.Any())
            {
                throw new Exception(String.Format("Unsupported JSON type for claim types: {0}", unsupportedJsonClaimTypes.Aggregate((x, y) => x + ", " + y)));
            }

            return payload;
        }

        /// <summary>
        /// Applies the signature to the JWT
        /// </summary>
        /// <param name="jwt">The JWT object.</param>
        /// <returns>The signed JWT</returns>
        protected virtual async Task<string> SignAsync(JwtSecurityToken jwt)
        {
            var rawDataBytes = System.Text.Encoding.UTF8.GetBytes(jwt.EncodedHeader + "." + jwt.EncodedPayload); // TODO: Is UTF-8 correct?

            var keyVaultSignatureProvider = new AzureKeyVaultSignatureProvider(_keyIdentifier, JsonWebKeySignatureAlgorithm.RS256, KeyVaultClientAuthenticationCallback);

            var rawSignature = await Task.Run(() => Convert.ToBase64String(keyVaultSignatureProvider.Sign(rawDataBytes))).ConfigureAwait(false);

            return jwt.EncodedHeader + "." + jwt.EncodedPayload + "." + rawSignature;

            //var handler = new JwtSecurityTokenHandler
            //{
            //    SignatureProviderFactory = new AzureKeyVaultSignatureProviderFactory()
            //};
            //return Task.FromResult(handler.WriteToken(jwt));
        }

        private async Task<string> KeyVaultClientAuthenticationCallback(string authority, string resource, string scope)
        {
            var authContext = new AuthenticationContext(authority);
            ClientCredential clientCred = new ClientCredential(_keyVaultClientId, _keyVaultClientSecret);
            AuthenticationResult result = await authContext.AcquireTokenAsync(resource, clientCred);

            if (result == null)
                throw new InvalidOperationException("Failed to obtain the JWT token");

            return result.AccessToken;
        }
    }
}